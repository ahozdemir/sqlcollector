using SqlDataCollector.Models;
using SqlDataCollector.Scheduling;

namespace SqlDataCollector.Tests.Integration;

/// <summary>Columnstore target storage: table layout, every load mode, daily partitions with retention, Evolve, limits.</summary>
[Collection(IntegrationCollection.Name)]
[Trait("Category", "Integration")]
public sealed class ColumnstoreTests(CollectorFixture fx)
{
    private const string Dwh = "DWH", Src = "SrcDb", Meta = "DataCollectorMeta";
    private static string Suffix() => Guid.NewGuid().ToString("N")[..6];

    /// <summary>A source table of its own (other tests rely on the shared seed rows).</summary>
    private async Task<string> SourceAsync(string prefix)
    {
        var name = $"{prefix}_{Suffix()}";
        await fx.ExecAsync(Src, $"""
            CREATE TABLE dbo.{name}(Id int NOT NULL PRIMARY KEY, Name nvarchar(20) NULL, Amount decimal(38,10) NULL, ModifiedAt datetime NOT NULL);
            INSERT dbo.{name} VALUES
                (1, N'Ayşe', 12345678901234567890.1234567890, '2026-01-01T10:00:00.003'),
                (2, N'Bora', -0.5, '2026-01-01T10:00:00.007'),
                (3, NULL,    NULL, '2026-01-01T10:00:00.010');
            """);
        return name;
    }

    private Task<ExtractJobDef> JobAsync(string sourceTable, Action<ExtractJobDef>? configure = null) =>
        fx.SaveAsync(fx.NewExtractJob($"cs_{sourceTable}", j =>
        {
            j.SourceTable = sourceTable; j.TargetSchema = "cs"; j.TargetStorage = TargetStorage.Columnstore; j.RetentionDays = 30;
            configure?.Invoke(j);
        }));

    private Task<int> Int(string sql) => fx.ScalarAsync<int>(Dwh, sql);

    /// <summary>Index types of a table: 1 = clustered, 2 = nonclustered, 5 = clustered columnstore.</summary>
    private async Task<string> IndexTypesAsync(string table) =>
        await fx.ScalarAsync<string>(Dwh, $"SELECT ISNULL(STRING_AGG(CAST(type AS varchar(3)), ',') WITHIN GROUP (ORDER BY type), '') FROM sys.indexes WHERE object_id = OBJECT_ID('{table}') AND type > 0");

    private Task<string> LastStatusAsync(ExtractJobDef job) => fx.ScalarAsync<string>(Meta,
        "SELECT TOP 1 CONCAT(Status, '|', ErrorMessage) FROM etl.LoadRun WHERE ExtractJobId = @id ORDER BY LoadRunId DESC", new { id = job.ExtractJobId });

    [Fact]
    public async Task Append_CreatesAClusteredColumnstoreWithoutOtherIndexes_AndKeepsValuesExact()
    {
        var t = await SourceAsync("CsAppend");
        var job = await JobAsync(t);
        await fx.RunExtractAsync(job);
        await fx.RunExtractAsync(job);

        Assert.Equal("5", await IndexTypesAsync($"cs.{job.TargetTable}"));   // only the clustered columnstore index
        Assert.Equal(6, await Int($"SELECT COUNT(*) FROM cs.{job.TargetTable}"));
        Assert.Equal(2, await Int($"SELECT COUNT(DISTINCT _LoadId) FROM cs.{job.TargetTable}"));
        Assert.Equal("12345678901234567890.1234567890",
            await fx.ScalarAsync<string>(Dwh, $"SELECT TOP 1 CONVERT(varchar(50), Amount) FROM cs.{job.TargetTable} WHERE Id = 1"));
        Assert.Equal("2026-01-01 10:00:00.003",
            await fx.ScalarAsync<string>(Dwh, $"SELECT TOP 1 CONVERT(varchar(30), ModifiedAt, 121) FROM cs.{job.TargetTable} WHERE Id = 1"));
        Assert.StartsWith("Succeeded", await LastStatusAsync(job));
    }

    [Fact]
    public async Task Replace_And_Incremental_WorkOnColumnstore()
    {
        var t = await SourceAsync("CsInc");
        var replace = await JobAsync(t, j => { j.LoadMode = LoadMode.Replace; j.TargetTable = t + "_r"; j.Name = "csr_" + t; });
        await fx.RunExtractAsync(replace);
        await fx.RunExtractAsync(replace);
        Assert.Equal(3, await Int($"SELECT COUNT(*) FROM cs.{t}_r"));             // older loads deleted from the columnstore
        Assert.Equal(1, await Int($"SELECT COUNT(DISTINCT _LoadId) FROM cs.{t}_r"));

        var inc = await JobAsync(t, j => { j.LoadMode = LoadMode.Incremental; j.WatermarkColumn = "ModifiedAt"; j.TargetTable = t + "_i"; j.Name = "csi_" + t; });
        await fx.RunExtractAsync(inc);
        await fx.ExecAsync(Src, $"INSERT dbo.{t} VALUES (4, N'Cem', 1, '2026-01-01T10:00:00.013');");
        await fx.RunExtractAsync(inc);
        await fx.RunExtractAsync(inc);   // nothing new: the datetime watermark keeps its .003 precision
        Assert.Equal(4, await Int($"SELECT COUNT(*) FROM cs.{t}_i"));
        Assert.Equal("5", await IndexTypesAsync($"cs.{t}_i"));
    }

    [Fact]
    public async Task DailyPartitions_AreAligned_AndRetentionTruncatesOldDays()
    {
        var t = await SourceAsync("CsPart");
        var job = await JobAsync(t, j => { j.PartitionDaily = true; j.RetentionDays = 1; });
        await fx.RunExtractAsync(job);

        // the columnstore index lives on the daily partition scheme
        Assert.Equal(1, await Int($"""
            SELECT COUNT(*) FROM sys.indexes i JOIN sys.partition_schemes ps ON ps.data_space_id = i.data_space_id
            WHERE i.object_id = OBJECT_ID('cs.{job.TargetTable}') AND i.type = 5 AND ps.name = 'ps_cs_{job.TargetTable}_daily'
            """));
        await fx.ExecAsync(Dwh, $"""
            INSERT cs.{job.TargetTable} (_LoadId, _LoadedAt, _ExtractJobId, _SourceServer, Id, Name, Amount, ModifiedAt)
            VALUES (-1, DATEADD(day, -5, SYSUTCDATETIME()), @job, 'SRC', 99, N'old', 0, '2020-01-01');
            """, new { job = job.ExtractJobId });

        await fx.Get<RetentionRunner>().RunAsync(CancellationToken.None);

        Assert.Equal(0, await Int($"SELECT COUNT(*) FROM cs.{job.TargetTable} WHERE Id = 99"));
        Assert.Equal(3, await Int($"SELECT COUNT(*) FROM cs.{job.TargetTable}"));
    }

    [Fact]
    public async Task Merge_And_Rebuild_KeepTheColumnstore()
    {
        var t = await SourceAsync("CsMerge");
        var merge = await JobAsync(t, j => { j.LoadMode = LoadMode.Merge; j.KeyColumns = "Id"; j.RetentionDays = null; j.TargetTable = t + "_m"; j.Name = "csm_" + t; });
        await fx.RunExtractAsync(merge);
        await fx.ExecAsync(Src, $"UPDATE dbo.{t} SET Name = N'Ayşe2' WHERE Id = 1; INSERT dbo.{t} VALUES (4, N'Cem', 1, '2026-01-02');");
        await fx.RunExtractAsync(merge);
        Assert.Equal(4, await Int($"SELECT COUNT(*) FROM cs.{t}_m"));
        Assert.Equal(1, await Int($"SELECT COUNT(*) FROM cs.{t}_m WHERE Id = 1 AND Name = N'Ayşe2'"));
        Assert.Equal("2,5", await IndexTypesAsync($"cs.{t}_m"));   // columnstore + the unique key index of Merge
        Assert.Equal(1, await Int($"SELECT COUNT(*) FROM sys.indexes WHERE object_id = OBJECT_ID('cs.{t}_m') AND name = 'UX_cs_{t}_m_Key' AND is_unique = 1"));

        var rebuild = await JobAsync(t, j => { j.LoadMode = LoadMode.Replace; j.ReplaceRebuild = true; j.RetentionDays = null; j.TargetTable = t + "_b"; j.Name = "csb_" + t; });
        await fx.RunExtractAsync(rebuild);
        await fx.RunExtractAsync(rebuild);
        Assert.Equal("5", await IndexTypesAsync($"cs.{t}_b"));
        Assert.Equal(4, await Int($"SELECT COUNT(*) FROM cs.{t}_b"));
        Assert.Equal(0, await Int($"SELECT COUNT(*) FROM sys.tables WHERE name LIKE '{t}_b__sdc%'"));
    }

    [Fact]
    public async Task Evolve_AddsAndWidensColumnsOfAColumnstoreTable()
    {
        var t = await SourceAsync("CsEvolve");
        var job = await JobAsync(t, j => j.SchemaPolicy = SchemaPolicy.Evolve);
        await fx.RunExtractAsync(job);
        await fx.ExecAsync(Src, $"ALTER TABLE dbo.{t} ALTER COLUMN Name nvarchar(60) NULL; ALTER TABLE dbo.{t} ADD City nvarchar(30) NULL;");
        await fx.ExecAsync(Src, $"UPDATE dbo.{t} SET City = N'İzmir', Name = REPLICATE(N'x', 50) WHERE Id = 2;");
        await fx.RunExtractAsync(job);

        Assert.StartsWith("Succeeded", await LastStatusAsync(job));
        Assert.Equal(120, await Int($"SELECT max_length FROM sys.columns WHERE object_id = OBJECT_ID('cs.{job.TargetTable}') AND name = 'Name'"));
        Assert.Equal(1, await Int($"SELECT COUNT(*) FROM cs.{job.TargetTable} WHERE City = N'İzmir' AND LEN(Name) = 50"));
        Assert.Equal("5", await IndexTypesAsync($"cs.{job.TargetTable}"));
    }

    [Fact]
    public async Task XmlColumns_FailWithAClearMessage_AndAnExistingTableKeepsItsStorage()
    {
        // the seed table dbo.Orders has an xml column
        var xml = await fx.SaveAsync(fx.NewExtractJob($"cs_xml_{Suffix()}", j => { j.TargetSchema = "cs"; j.TargetStorage = TargetStorage.Columnstore; }));
        await Assert.ThrowsAnyAsync<Exception>(() => fx.RunExtractAsync(xml));
        var status = await LastStatusAsync(xml);
        Assert.StartsWith("Failed|", status);
        Assert.Contains("cannot hold xml or sql_variant columns (Xmldata)", status);
        Assert.Equal(0, await Int($"SELECT COUNT(*) FROM sys.tables WHERE name = '{xml.TargetTable}'"));

        // a rowstore table stays rowstore when the job is switched to columnstore later (it is never converted)
        var t = await SourceAsync("CsKeep");
        var job = await JobAsync(t, j => j.TargetStorage = TargetStorage.Rowstore);
        await fx.RunExtractAsync(job);
        job.TargetStorage = TargetStorage.Columnstore;
        await fx.Repo.SaveExtractJobAsync(job, "tests");
        await fx.RunExtractAsync(job);
        Assert.Equal("1,2,2", await IndexTypesAsync($"cs.{job.TargetTable}"));
        Assert.Equal(6, await Int($"SELECT COUNT(*) FROM cs.{job.TargetTable}"));
    }
}
