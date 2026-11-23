using Dapper;
using SqlDataCollector.Extract;
using SqlDataCollector.Infrastructure;
using SqlDataCollector.Models;

namespace SqlDataCollector.Tests.Integration;

/// <summary>Parallel reads: several source readers, one target stream – every row exactly once, failures roll back.</summary>
[Collection(IntegrationCollection.Name)]
[Trait("Category", "Integration")]
public sealed class ParallelReadTests(CollectorFixture fx)
{
    private const string Src = "SrcDb";
    private const string Dwh = "DWH";
    private const string Meta = "DataCollectorMeta";
    private const int Rows = 20_000;

    /// <summary>20,000 rows: int primary key, datetime, decimal(38,10) with values beyond .NET decimal, NULL notes.</summary>
    private async Task<string> SourceAsync(string name, bool heap = false)
    {
        await fx.ExecAsync(Src, $"""
            DROP TABLE IF EXISTS dbo.{name};
            CREATE TABLE dbo.{name}(Id int NOT NULL {(heap ? "" : "PRIMARY KEY")}, CreatedAt datetime NULL, Amount decimal(38,10) NULL, Note nvarchar(50) NULL);
            WITH n AS (SELECT TOP ({Rows}) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS i FROM sys.all_objects a CROSS JOIN sys.all_objects b)
            INSERT dbo.{name}
            SELECT i, CASE WHEN {(heap ? "i % 500 = 0" : "1 = 0")} THEN NULL ELSE DATEADD(MINUTE, i, '2026-01-01') END,
                   CASE WHEN i % 1000 = 0 THEN 12345678901234567890.1234567890 ELSE i / 3.0 END,
                   CASE WHEN i % 7 = 0 THEN NULL ELSE CONCAT(N'n', i) END
            FROM n;
            """);
        return name;
    }

    private Task<ExtractJobDef> JobAsync(string table, Action<ExtractJobDef>? configure = null) =>
        fx.SaveAsync(fx.NewExtractJob(table, j =>
        {
            j.SourceTable = table; j.TargetSchema = "par"; j.ReadParallelism = 4; j.BatchSize = 5000; j.RetentionDays = null;
            configure?.Invoke(j);
        }));

    private Task<(long Rows, string Status)> LastRunAsync(ExtractJobDef job) =>
        fx.Db(Meta).QuerySingleAsync<(long, string)>(
            "SELECT TOP 1 ISNULL(RowsLoaded, -1), Status FROM etl.LoadRun WHERE ExtractJobId = @id ORDER BY LoadRunId DESC", new { id = job.ExtractJobId });

    [Fact]
    public async Task Reader_SplitsOnThePrimaryKey_AndReturnsEveryRowOnce()
    {
        var t = await SourceAsync("P_Reader");
        var job = fx.NewExtractJob(t, j => { j.SourceTable = t; j.ReadParallelism = 4; });
        var server = (await fx.Repo.GetServerAsync(fx.SourceServerId))!;

        await using var reader = await ParallelSourceReader.OpenAsync(fx.Get<SqlConnectionFactory>(), server, job, null, false, CancellationToken.None);
        Assert.NotNull(reader);
        Assert.Equal(4, reader!.Readers);
        Assert.Contains("split on Id (1 – 20000)", reader.Description);
        var ids = new HashSet<int>();
        int big = 0;
        while (reader.Read())
        {
            Assert.True(ids.Add(reader.GetInt32(0)));
            if (reader.GetValue(2) is System.Data.SqlTypes.SqlDecimal d && d.ToString().StartsWith("12345678901234567890")) big++;   // beyond .NET decimal
        }
        Assert.Equal(Rows, ids.Count);
        Assert.Equal(Rows / 1000, big);
    }

    [Fact]
    public async Task Append_WithFourReaders_LoadsEveryRowExactlyOnce_WithExactDecimals()
    {
        var t = await SourceAsync("P_Append");
        var job = await JobAsync(t);
        await fx.RunExtractAsync(job);

        Assert.Equal((Rows, "Succeeded"), await LastRunAsync(job));
        Assert.Equal(Rows, await fx.ScalarAsync<int>(Dwh, $"SELECT COUNT(DISTINCT Id) FROM par.{t}"));
        Assert.Equal(Rows, await fx.ScalarAsync<int>(Dwh, $"SELECT COUNT(*) FROM par.{t}"));
        Assert.Equal(await fx.ScalarAsync<string>(Src, $"SELECT CONVERT(varchar(60), SUM(Amount)) FROM dbo.{t}"),
                     await fx.ScalarAsync<string>(Dwh, $"SELECT CONVERT(varchar(60), SUM(Amount)) FROM par.{t}"));
        Assert.Equal(await fx.ScalarAsync<int>(Src, $"SELECT COUNT(*) FROM dbo.{t} WHERE Note IS NULL"),
                     await fx.ScalarAsync<int>(Dwh, $"SELECT COUNT(*) FROM par.{t} WHERE Note IS NULL"));
    }

    [Fact]
    public async Task Incremental_WithParallelReaders_ReadsOnlyNewRows_AndKeepsTheMaximumWatermark()
    {
        var t = await SourceAsync("P_Incr");
        var job = await JobAsync(t, j => { j.LoadMode = LoadMode.Incremental; j.WatermarkColumn = "CreatedAt"; });
        await fx.RunExtractAsync(job);
        Assert.Equal((Rows, "Succeeded"), await LastRunAsync(job));
        Assert.Equal("2026-01-14T21:20:00.0000000", (await fx.Repo.GetExtractStateAsync(job.ExtractJobId))!.WatermarkValue);   // 1 Jan + 20,000 min

        await fx.ExecAsync(Src, $"INSERT dbo.{t} SELECT {Rows} + Id, DATEADD(DAY, 30, CreatedAt), 1, N'new' FROM dbo.{t} WHERE Id <= 500;");
        await fx.RunExtractAsync(job);
        Assert.Equal((500L, "Succeeded"), await LastRunAsync(job));
        Assert.Equal(Rows + 500, await fx.ScalarAsync<int>(Dwh, $"SELECT COUNT(DISTINCT Id) FROM par.{t}"));
    }

    [Fact]
    public async Task Heap_NeedsASplitColumn_AndNullSplitValuesAreRead()
    {
        var t = await SourceAsync("P_Heap", heap: true);
        var job = await JobAsync(t);
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => fx.RunExtractAsync(job));
        Assert.Contains("no clustered index or primary key", ex.Message);

        job.SplitColumn = "CreatedAt";   // datetime with NULLs: the first slice takes them
        await fx.Repo.SaveExtractJobAsync(job, "tests");
        await fx.RunExtractAsync(job);
        Assert.Equal((Rows, "Succeeded"), await LastRunAsync(job));
        Assert.Equal(Rows / 500, await fx.ScalarAsync<int>(Dwh, $"SELECT COUNT(*) FROM par.{t} WHERE CreatedAt IS NULL AND _LoadId = (SELECT MAX(_LoadId) FROM par.{t})"));
    }

    [Fact]
    public async Task Merge_WithParallelReaders_StagesAllRowsOnce()
    {
        var t = await SourceAsync("P_Merge");
        var job = await JobAsync(t, j => { j.LoadMode = LoadMode.Merge; j.KeyColumns = "Id"; });
        await fx.RunExtractAsync(job);
        Assert.Equal((Rows, "Succeeded"), await LastRunAsync(job));

        await fx.ExecAsync(Src, $"UPDATE dbo.{t} SET Note = N'changed' WHERE Id % 100 = 0;");
        await fx.RunExtractAsync(job);
        Assert.Equal(Rows / 100, await fx.ScalarAsync<long>(Meta,
            "SELECT TOP 1 RowsUpdated FROM etl.LoadRun WHERE ExtractJobId = @id ORDER BY LoadRunId DESC", new { id = job.ExtractJobId }));
        Assert.Equal(Rows, await fx.ScalarAsync<int>(Dwh, $"SELECT COUNT(*) FROM par.{t}"));
    }

    [Fact]
    public async Task OneFailingReader_FailsTheRun_AndRollsBackTheRowsOfTheOthers()
    {
        var t = await SourceAsync("P_Fail");
        // only the last slice hits the conversion error (the MIN/MAX probe does not read the column list); the other readers send rows
        var job = await JobAsync(t, j => j.ColumnList = $"Id, CreatedAt, Amount, Note, CAST(CASE WHEN Id > {Rows - 100} THEN 'x' ELSE '1' END AS int) AS Bad");
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => fx.RunExtractAsync(job));
        Assert.Contains("Conversion failed", ex.Message + ex.InnerException?.Message);
        Assert.Equal("Failed", (await LastRunAsync(job)).Status);
        Assert.Equal(0, await fx.ScalarAsync<int>(Dwh, $"SELECT COUNT(*) FROM par.{t}"));
    }
}
