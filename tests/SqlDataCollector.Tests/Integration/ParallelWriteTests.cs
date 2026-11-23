using Dapper;
using SqlDataCollector.Models;

namespace SqlDataCollector.Tests.Integration;

/// <summary>Parallel writes: one reader, N bulk copies into the target – every row once, one _LoadId, failures roll back.</summary>
[Collection(IntegrationCollection.Name)]
[Trait("Category", "Integration")]
public sealed class ParallelWriteTests(CollectorFixture fx)
{
    private const string Src = "SrcDb";
    private const string Dwh = "DWH";
    private const string Meta = "DataCollectorMeta";
    private const int Rows = 30_000;

    private async Task<string> SourceAsync(string name)
    {
        await fx.ExecAsync(Src, $"""
            DROP TABLE IF EXISTS dbo.{name};
            CREATE TABLE dbo.{name}(Id int NOT NULL PRIMARY KEY, CreatedAt datetime NOT NULL, Amount decimal(38,10) NULL, Note nvarchar(50) NULL);
            WITH n AS (SELECT TOP ({Rows}) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS i FROM sys.all_objects a CROSS JOIN sys.all_objects b)
            INSERT dbo.{name}
            SELECT i, DATEADD(MINUTE, i, '2026-01-01'),
                   CASE WHEN i % 1000 = 0 THEN 12345678901234567890.1234567890 ELSE i / 7.0 END,
                   CASE WHEN i % 5 = 0 THEN NULL ELSE CONCAT(N'n', i) END
            FROM n;
            """);
        return name;
    }

    private Task<ExtractJobDef> JobAsync(string table, Action<ExtractJobDef>? configure = null) =>
        fx.SaveAsync(fx.NewExtractJob(table, j =>
        {
            j.SourceTable = table; j.TargetSchema = "pw"; j.WriteParallelism = 4; j.BatchSize = 2000; j.RetentionDays = null;
            configure?.Invoke(j);
        }));

    private Task<(long Rows, string Status)> LastRunAsync(ExtractJobDef job) =>
        fx.Db(Meta).QuerySingleAsync<(long, string)>(
            "SELECT TOP 1 ISNULL(RowsLoaded, -1), Status FROM etl.LoadRun WHERE ExtractJobId = @id ORDER BY LoadRunId DESC", new { id = job.ExtractJobId });

    private async Task AssertCopiedAsync(string table, string target)
    {
        Assert.Equal(Rows, await fx.ScalarAsync<int>(Dwh, $"SELECT COUNT(*) FROM {target}"));
        Assert.Equal(Rows, await fx.ScalarAsync<int>(Dwh, $"SELECT COUNT(DISTINCT Id) FROM {target}"));
        Assert.Equal(1, await fx.ScalarAsync<int>(Dwh, $"SELECT COUNT(DISTINCT _LoadId) FROM {target}"));   // one load, whatever the writer
        Assert.Equal(await fx.ScalarAsync<string>(Src, $"SELECT CONVERT(varchar(60), SUM(Amount)) FROM dbo.{table}"),
                     await fx.ScalarAsync<string>(Dwh, $"SELECT CONVERT(varchar(60), SUM(Amount)) FROM {target}"));
        Assert.Equal(Rows / 5, await fx.ScalarAsync<int>(Dwh, $"SELECT COUNT(*) FROM {target} WHERE Note IS NULL"));
    }

    [Theory]
    [InlineData(TargetStorage.Rowstore)]
    [InlineData(TargetStorage.Columnstore)]
    public async Task Append_WithFourWriters_LoadsEveryRowOnce(TargetStorage storage)
    {
        var t = await SourceAsync($"PW_Append_{storage}");
        var job = await JobAsync(t, j => j.TargetStorage = storage);
        await fx.RunExtractAsync(job);
        Assert.Equal((Rows, "Succeeded"), await LastRunAsync(job));
        await AssertCopiedAsync(t, $"pw.{t}");
    }

    [Fact]
    public async Task ReplaceRebuild_WithFourWriters_SwapsInTheCompleteTable()
    {
        var t = await SourceAsync("PW_Rebuild");
        var job = await JobAsync(t, j => { j.LoadMode = LoadMode.Replace; j.ReplaceRebuild = true; j.TargetStorage = TargetStorage.Columnstore; });
        await fx.RunExtractAsync(job);
        await fx.RunExtractAsync(job);
        Assert.Equal((Rows, "Succeeded"), await LastRunAsync(job));
        await AssertCopiedAsync(t, $"pw.{t}");
        Assert.Equal(0, await fx.ScalarAsync<int>(Dwh, $"SELECT COUNT(*) FROM sys.tables WHERE name = '{t}__sdc_new'"));
    }

    [Fact]
    public async Task ParallelReadersAndWriters_Together_KeepTheWatermark()
    {
        var t = await SourceAsync("PW_Incr");
        var job = await JobAsync(t, j => { j.LoadMode = LoadMode.Incremental; j.WatermarkColumn = "CreatedAt"; j.ReadParallelism = 4; });
        await fx.RunExtractAsync(job);
        Assert.Equal((Rows, "Succeeded"), await LastRunAsync(job));
        await AssertCopiedAsync(t, $"pw.{t}");
        Assert.Equal("2026-01-21T20:00:00.0000000", (await fx.Repo.GetExtractStateAsync(job.ExtractJobId))!.WatermarkValue);   // 1 Jan + 30,000 min

        await fx.ExecAsync(Src, $"INSERT dbo.{t} SELECT {Rows} + Id, DATEADD(DAY, 60, CreatedAt), 1, N'new' FROM dbo.{t} WHERE Id <= 3000;");
        await fx.RunExtractAsync(job);
        Assert.Equal((3000L, "Succeeded"), await LastRunAsync(job));
    }

    [Fact]
    public async Task FailingWriter_FailsTheRun_AndRemovesTheRowsOfThatLoadOnly()
    {
        var t = await SourceAsync("PW_WriterFails");
        var job = await JobAsync(t);
        await fx.RunExtractAsync(job);
        var firstLoad = await fx.ScalarAsync<long>(Dwh, $"SELECT MAX(_LoadId) FROM pw.{t}");

        // a second Append of the same ids violates this index in whichever writer gets a row first
        await fx.ExecAsync(Dwh, $"CREATE UNIQUE NONCLUSTERED INDEX UX_pw_{t}_Id ON pw.{t}(Id);");
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => fx.RunExtractAsync(job));
        Assert.Contains("duplicate key", ex.Message + ex.InnerException?.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Failed", (await LastRunAsync(job)).Status);
        Assert.Equal(Rows, await fx.ScalarAsync<int>(Dwh, $"SELECT COUNT(*) FROM pw.{t}"));
        Assert.Equal(firstLoad, await fx.ScalarAsync<long>(Dwh, $"SELECT MAX(_LoadId) FROM pw.{t}"));
    }

    [Fact]
    public async Task FailingSource_StopsTheWriters_AndRollsBack()
    {
        var t = await SourceAsync("PW_SourceFails");
        var job = await JobAsync(t, j => j.ColumnList = $"Id, CreatedAt, Amount, Note, CAST(CASE WHEN Id > {Rows - 50} THEN 'x' ELSE '1' END AS int) AS Bad");
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => fx.RunExtractAsync(job));
        Assert.Contains("Conversion failed", ex.Message + ex.InnerException?.Message);
        Assert.Equal("Failed", (await LastRunAsync(job)).Status);
        Assert.Equal(0, await fx.ScalarAsync<int>(Dwh, $"SELECT COUNT(*) FROM pw.{t}"));
    }

    [Fact]
    public async Task Merge_IgnoresParallelWriters()
    {
        var t = await SourceAsync("PW_Merge");
        var job = await JobAsync(t, j => { j.LoadMode = LoadMode.Merge; j.KeyColumns = "Id"; });   // saved without the editor's normalization
        Assert.Equal(4, job.WriteParallelism);
        await fx.RunExtractAsync(job);
        Assert.Equal((Rows, "Succeeded"), await LastRunAsync(job));
        await AssertCopiedAsync(t, $"pw.{t}");
    }
}
