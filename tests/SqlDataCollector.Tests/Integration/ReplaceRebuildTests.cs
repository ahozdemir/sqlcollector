using SqlDataCollector.Models;

namespace SqlDataCollector.Tests.Integration;

/// <summary>Replace with rebuild: load a new table, swap it in atomically, keep the old one on failure.</summary>
[Collection(IntegrationCollection.Name)]
[Trait("Category", "Integration")]
public sealed class ReplaceRebuildTests(CollectorFixture fx)
{
    private const string Dwh = "DWH";

    private Task<int> CountAsync(string sql) => fx.ScalarAsync<int>(Dwh, sql);

    private Task<ExtractJobDef> RebuildJobAsync(string table, Action<ExtractJobDef>? configure = null) =>
        fx.SaveAsync(fx.NewExtractJob(table, j =>
        {
            j.SourceTable = "Customers"; j.LoadMode = LoadMode.Replace; j.ReplaceRebuild = true; j.TargetSchema = "rb";
            configure?.Invoke(j);
        }));

    [Fact]
    public async Task Rebuild_KeepsOnlyTheLatestLoad_WithTheUsualIndexes_AndNoLeftovers()
    {
        var job = await RebuildJobAsync("T_Rebuild");
        await fx.RunExtractAsync(job);
        await fx.RunExtractAsync(job);

        Assert.Equal(3, await CountAsync("SELECT COUNT(*) FROM rb.T_Rebuild"));
        Assert.Equal(1, await CountAsync("SELECT COUNT(DISTINCT _LoadId) FROM rb.T_Rebuild"));
        Assert.Equal(job.ExtractJobId, await CountAsync("SELECT MAX(_ExtractJobId) FROM rb.T_Rebuild"));
        Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM sys.tables WHERE name LIKE 'T_Rebuild[_][_]sdc%'"));
        Assert.Equal(1, await CountAsync("SELECT COUNT(*) FROM sys.indexes WHERE object_id = OBJECT_ID('rb.T_Rebuild') AND name = 'CIX_rb_T_Rebuild'"));
        Assert.Equal("Succeeded", await fx.ScalarAsync<string>("DataCollectorMeta",
            "SELECT TOP 1 Status FROM etl.LoadRun WHERE ExtractJobId = @id ORDER BY LoadRunId DESC", new { id = job.ExtractJobId }));
    }

    [Fact]
    public async Task Rebuild_AbsorbsSourceSchemaChanges_EvenWithStrictPolicy()
    {
        var job = await RebuildJobAsync("T_RebuildSchema", j =>
        {
            j.SourceType = SourceType.Query; j.SourceTable = null; j.SchemaPolicy = SchemaPolicy.Strict;
            j.SourceQuery = "SELECT CustomerId, Name FROM dbo.Customers";
        });
        await fx.RunExtractAsync(job);

        job.SourceQuery = "SELECT CAST(CustomerId AS bigint) AS CustomerId, City, 1 AS NewCol FROM dbo.Customers";   // type change, column dropped and added
        await fx.Repo.SaveExtractJobAsync(job, "tests");
        await fx.RunExtractAsync(job);

        Assert.Equal(3, await CountAsync("SELECT COUNT(*) FROM rb.T_RebuildSchema WHERE NewCol = 1"));
        Assert.Equal("bigint", await fx.ScalarAsync<string>(Dwh,
            "SELECT t.name FROM sys.columns c JOIN sys.types t ON t.user_type_id = c.user_type_id WHERE c.object_id = OBJECT_ID('rb.T_RebuildSchema') AND c.name = 'CustomerId'"));
        Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID('rb.T_RebuildSchema') AND name = 'Name'"));
    }

    [Fact]
    public async Task FailedLoad_KeepsThePreviousTable_AndDropsTheStagingTable()
    {
        var job = await RebuildJobAsync("T_RebuildFail", j =>
        {
            j.SourceType = SourceType.Query; j.SourceTable = null; j.SourceQuery = "SELECT CustomerId, Name FROM dbo.Customers";
        });
        await fx.RunExtractAsync(job);
        var loadBefore = await CountAsync("SELECT MAX(_LoadId) FROM rb.T_RebuildFail");

        // fails while rows are being read (after the staging table was created)
        job.SourceQuery = "SELECT CustomerId, CAST(CASE WHEN CustomerId > 1 THEN 'x' ELSE '1' END AS int) AS Name FROM dbo.Customers";
        await fx.Repo.SaveExtractJobAsync(job, "tests");
        await Assert.ThrowsAnyAsync<Exception>(() => fx.RunExtractAsync(job));

        Assert.Equal(3, await CountAsync("SELECT COUNT(*) FROM rb.T_RebuildFail"));
        Assert.Equal(loadBefore, await CountAsync("SELECT MAX(_LoadId) FROM rb.T_RebuildFail"));
        Assert.Equal(0, await CountAsync("SELECT COUNT(*) FROM sys.tables WHERE name LIKE 'T_RebuildFail[_][_]sdc%'"));
        Assert.Equal("Failed", await fx.ScalarAsync<string>("DataCollectorMeta",
            "SELECT TOP 1 Status FROM etl.LoadRun WHERE ExtractJobId = @id ORDER BY LoadRunId DESC", new { id = job.ExtractJobId }));
    }

    [Fact]
    public async Task ExistingTableWithoutLoadId_IsNeverReplaced()
    {
        await fx.ExecAsync(Dwh, """
            IF SCHEMA_ID('rb') IS NULL EXEC('CREATE SCHEMA rb');
            IF OBJECT_ID('rb.T_Foreign') IS NULL BEGIN CREATE TABLE rb.T_Foreign (X int); INSERT rb.T_Foreign VALUES (42); END
            """);
        var job = await RebuildJobAsync("T_Foreign");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => fx.RunExtractAsync(job));
        Assert.Contains("only replaces tables created by SqlDataCollector", ex.Message);
        Assert.Equal(42, await CountAsync("SELECT MAX(X) FROM rb.T_Foreign"));
    }

    [Fact]
    public async Task SharedTargetTable_IsRefused()
    {
        var other = await fx.SaveAsync(fx.NewExtractJob("T_RebuildShared", j => { j.SourceTable = "Customers"; j.TargetSchema = "rb"; }));
        await fx.RunExtractAsync(other);
        var job = await RebuildJobAsync("T_RebuildShared");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => fx.RunExtractAsync(job));
        Assert.Contains(other.Name, ex.Message);
        Assert.Equal(3, await CountAsync("SELECT COUNT(*) FROM rb.T_RebuildShared"));
    }
}
