using Dapper;
using SqlDataCollector.Models;
using SqlDataCollector.Services;

namespace SqlDataCollector.Tests.Integration;

/// <summary>Data quality checks after each load: results, the load's quality status, read-only custom SQL, dependents.</summary>
[Collection(IntegrationCollection.Name)]
[Trait("Category", "Integration")]
public sealed class QualityTests(CollectorFixture fx)
{
    private const string Src = "SrcDb";
    private const string Meta = "DataCollectorMeta";

    private async Task SourceAsync(string name) => await fx.ExecAsync(Src, $"""
        DROP TABLE IF EXISTS dbo.{name};
        CREATE TABLE dbo.{name}(Id int NOT NULL PRIMARY KEY, Name nvarchar(50) NULL, Amount decimal(18,2) NOT NULL);
        INSERT dbo.{name} VALUES (1, N'a', 10), (2, N'b', 20), (3, NULL, 30), (4, N'd', 40), (5, N'e', 50);
        """);

    private Task<ExtractJobDef> JobAsync(string table, LoadMode mode = LoadMode.Replace, Action<ExtractJobDef>? configure = null) =>
        fx.SaveAsync(fx.NewExtractJob(table, j =>
        {
            j.SourceTable = table; j.LoadMode = mode; j.TargetSchema = "qa";
            if (mode == LoadMode.Merge) j.KeyColumns = "Id";
            configure?.Invoke(j);
        }));

    private async Task<int> CheckAsync(ExtractJobDef job, string type, decimal? threshold, string severity = "Error",
        string? column = null, string scope = "Load", string? sql = null, string? op = null) =>
        await fx.Repo.SaveQualityCheckAsync(new QualityCheckDef
        {
            ExtractJobId = job.ExtractJobId, CheckType = type, Threshold = threshold, Severity = severity,
            ColumnName = column, Scope = scope, SqlText = sql, Operator = op
        }, "tests");

    private async Task<(string? Quality, Dictionary<int, (string Status, string? Message)> Results)> LastLoadAsync(ExtractJobDef job)
    {
        await using var c = fx.Db(Meta);
        var (loadId, quality) = await c.QuerySingleAsync<(long, string?)>(
            "SELECT TOP 1 LoadRunId, QualityStatus FROM etl.LoadRun WHERE ExtractJobId = @id ORDER BY LoadRunId DESC", new { id = job.ExtractJobId });
        var rows = await c.QueryAsync<(int CheckId, string Status, string? Message)>(
            "SELECT QualityCheckId, Status, Message FROM etl.QualityResult WHERE LoadRunId = @loadId", new { loadId });
        return (quality, rows.ToDictionary(r => r.CheckId, r => (r.Status, r.Message)));
    }

    [Fact]
    public async Task Checks_AreRecordedPerLoad_AndTheWorstResultIsTheLoadsQualityStatus()
    {
        await SourceAsync("Q_Basic");
        var job = await JobAsync("Q_Basic");
        var minRows = await CheckAsync(job, QualityCheckTypes.MinRows, 1);
        var source = await CheckAsync(job, QualityCheckTypes.SourceCount, 0);
        var nulls = await CheckAsync(job, QualityCheckTypes.NullRate, 10, severity: "Warning", column: "Name");   // 1 of 5 = 20 %
        var dups = await CheckAsync(job, QualityCheckTypes.Duplicates, 0, column: "Id");
        var custom = await CheckAsync(job, QualityCheckTypes.CustomSql, 0, sql: "SELECT COUNT(*) FROM {Table} WHERE _LoadId = @LoadId AND Amount < 0", op: "=");

        await fx.RunExtractAsync(job);
        var (quality, results) = await LastLoadAsync(job);
        Assert.Equal("Warning", quality);
        Assert.Equal("Passed", results[minRows].Status);
        Assert.Equal("Passed", results[source].Status);
        Assert.Contains("Source 5 row(s), target 5 row(s)", results[source].Message);
        Assert.Equal("Warning", results[nulls].Status);
        Assert.Contains("1 of 5 row(s) NULL (20 %", results[nulls].Message);
        Assert.Equal("Passed", results[dups].Status);
        Assert.Equal("Passed", results[custom].Status);

        // a business rule broken in the source -> the custom check fails -> the load's quality is Failed
        await fx.ExecAsync(Src, "UPDATE dbo.Q_Basic SET Amount = -1 WHERE Id = 2;");
        await fx.RunExtractAsync(job);
        (quality, results) = await LastLoadAsync(job);
        Assert.Equal("Failed", quality);
        Assert.Equal("Failed", results[custom].Status);
        Assert.Contains("returned 1; expected = 0", results[custom].Message);
        Assert.Equal("Succeeded", await fx.ScalarAsync<string>(Meta,
            "SELECT TOP 1 Status FROM etl.LoadRun WHERE ExtractJobId = @id ORDER BY LoadRunId DESC", new { id = job.ExtractJobId }));   // the load itself stays
        await fx.Repo.DeleteExtractJobAsync(job.ExtractJobId, "tests");
    }

    [Fact]
    public async Task CustomSql_RunsReadOnly_ACheckCannotChangeData()
    {
        await SourceAsync("Q_ReadOnly");
        var job = await JobAsync("Q_ReadOnly");
        var check = await CheckAsync(job, QualityCheckTypes.CustomSql, 0, sql: "DELETE FROM {Table}; SELECT 0", op: "=");

        await fx.RunExtractAsync(job);
        var (quality, results) = await LastLoadAsync(job);
        Assert.Equal("Error", results[check].Status);
        Assert.Contains("could not run", results[check].Message);
        Assert.Equal("Failed", quality);
        Assert.Equal(5, await fx.ScalarAsync<int>("DWH", "SELECT COUNT(*) FROM qa.Q_ReadOnly"));
        await fx.Repo.DeleteExtractJobAsync(job.ExtractJobId, "tests");
    }

    [Fact]
    public async Task RowChange_ComparesWithRecentLoads_AndSourceCount_WorksForMerge()
    {
        await SourceAsync("Q_Change");
        var job = await JobAsync("Q_Change", LoadMode.Merge);
        var change = await CheckAsync(job, QualityCheckTypes.RowChange, 50);
        var source = await CheckAsync(job, QualityCheckTypes.SourceCount, 0);

        for (int i = 0; i < 3; i++) await fx.RunExtractAsync(job);   // history: 5, 5, 5
        var (_, results) = await LastLoadAsync(job);
        Assert.Contains("Not enough history", results[change].Message);   // the 3rd load had only 2 earlier loads
        Assert.Equal("Passed", results[source].Status);
        Assert.Contains("(current state)", results[source].Message);

        await fx.ExecAsync(Src, "DELETE dbo.Q_Change WHERE Id > 1;");   // 1 row: 80 % below the average
        await fx.RunExtractAsync(job);
        (_, results) = await LastLoadAsync(job);
        Assert.Equal("Failed", results[change].Status);
        Assert.Contains("80 % deviation", results[change].Message);
        Assert.Equal("Failed", results[source].Status);   // merge keeps the 4 vanished keys: target 5, source 1
        await fx.Repo.DeleteExtractJobAsync(job.ExtractJobId, "tests");
    }

    [Fact]
    public async Task FailedQuality_KeepsDependentJobsWaiting()
    {
        await SourceAsync("Q_Upstream");
        var up = await JobAsync("Q_Upstream");
        var down = await JobAsync("Q_Downstream", configure: j => { j.SourceTable = "Q_Upstream"; j.TriggerMode = JobTriggerMode.AfterJobs; });
        var deps = fx.Get<DependencyService>();
        try
        {
            await deps.SaveAsync(new JobRef(JobRef.Extract, down.ExtractJobId), down.TriggerMode, [new JobRef(JobRef.Extract, up.ExtractJobId)], "tests");
            var check = await CheckAsync(up, QualityCheckTypes.MinRows, 10);   // 5 rows -> fails

            await fx.RunExtractAsync(up);
            Assert.Equal("Failed", (await LastLoadAsync(up)).Quality);
            Assert.Empty(fx.Launcher.For(new JobRef(JobRef.Extract, down.ExtractJobId)));

            await fx.Repo.SaveQualityCheckAsync(new QualityCheckDef
            {
                QualityCheckId = check, ExtractJobId = up.ExtractJobId, CheckType = QualityCheckTypes.MinRows, Threshold = 1
            }, "tests");
            await fx.RunExtractAsync(up);
            Assert.Equal("Passed", (await LastLoadAsync(up)).Quality);
            Assert.Single(fx.Launcher.For(new JobRef(JobRef.Extract, down.ExtractJobId)));
        }
        finally
        {
            await fx.Repo.DeleteExtractJobAsync(down.ExtractJobId, "tests");
            await fx.Repo.DeleteExtractJobAsync(up.ExtractJobId, "tests");
        }
    }

    [Fact]
    public async Task AnEarlierGoodLoad_DoesNotReleaseDependents_WhenTheLatestLoadFailedItsChecks()
    {
        await SourceAsync("Q_UpA");
        await SourceAsync("Q_UpB");
        var a = await JobAsync("Q_UpA");
        var b = await JobAsync("Q_UpB");
        var down = await JobAsync("Q_After", configure: j => { j.SourceTable = "Q_UpA"; j.TriggerMode = JobTriggerMode.AfterJobs; });
        var downRef = new JobRef(JobRef.Extract, down.ExtractJobId);
        var deps = fx.Get<DependencyService>();
        try
        {
            await deps.SaveAsync(downRef, down.TriggerMode, [new JobRef(JobRef.Extract, a.ExtractJobId), new JobRef(JobRef.Extract, b.ExtractJobId)], "tests");
            var check = await CheckAsync(a, QualityCheckTypes.MinRows, 1);

            await fx.RunExtractAsync(a);                                   // good load of A
            await fx.Repo.SaveQualityCheckAsync(new QualityCheckDef
            {
                QualityCheckId = check, ExtractJobId = a.ExtractJobId, CheckType = QualityCheckTypes.MinRows, Threshold = 10
            }, "tests");
            await fx.RunExtractAsync(a);                                   // A again: fails its check, the table now holds this load
            Assert.Equal("Failed", (await LastLoadAsync(a)).Quality);

            await fx.RunExtractAsync(b);                                   // B completes the round
            Assert.Empty(fx.Launcher.For(downRef));                        // the earlier good load of A does not count

            await fx.Repo.SaveQualityCheckAsync(new QualityCheckDef
            {
                QualityCheckId = check, ExtractJobId = a.ExtractJobId, CheckType = QualityCheckTypes.MinRows, Threshold = 1
            }, "tests");
            await fx.RunExtractAsync(a);                                   // A passes again -> the dependent starts
            Assert.Single(fx.Launcher.For(downRef));
        }
        finally
        {
            await fx.Repo.DeleteExtractJobAsync(down.ExtractJobId, "tests");
            await fx.Repo.DeleteExtractJobAsync(a.ExtractJobId, "tests");
            await fx.Repo.DeleteExtractJobAsync(b.ExtractJobId, "tests");
        }
    }
}
