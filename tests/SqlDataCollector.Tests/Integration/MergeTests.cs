using Dapper;
using SqlDataCollector.Export;
using SqlDataCollector.Models;

namespace SqlDataCollector.Tests.Integration;

/// <summary>Merge (upsert) load mode: one row per key, only changed rows rewritten, optional delete of missing keys.</summary>
[Collection(IntegrationCollection.Name)]
[Trait("Category", "Integration")]
public sealed class MergeTests(CollectorFixture fx)
{
    private const string Src = "SrcDb";
    private const string Dwh = "DWH";
    private const string Meta = "DataCollectorMeta";

    /// <summary>A fresh source table with 3 rows (one NULL note, one xml document, a decimal(38,10)).</summary>
    private async Task<string> SourceAsync(string name)
    {
        await fx.ExecAsync(Src, $"""
            DROP TABLE IF EXISTS dbo.{name};
            CREATE TABLE dbo.{name}(Id int NOT NULL PRIMARY KEY, Name nvarchar(50) NOT NULL, Amount decimal(38,10) NULL,
                                    Doc xml NULL, Note nvarchar(50) NULL, ModifiedAt datetime NOT NULL);
            INSERT dbo.{name} VALUES
                (1, N'Ayşe',  12345678901234567890.1234567890, N'<a x="1"/>', NULL,  '2026-01-01T10:00:00'),
                (2, N'Bora',  1.5,                              NULL,           N'n2', '2026-01-01T10:00:00'),
                (3, N'Cem',   NULL,                             N'<b/>',        NULL,  '2026-01-01T10:00:00');
            """);
        return name;
    }

    private Task<ExtractJobDef> MergeJobAsync(string sourceTable, Action<ExtractJobDef>? configure = null) =>
        fx.SaveAsync(fx.NewExtractJob(sourceTable, j =>
        {
            j.SourceTable = sourceTable; j.LoadMode = LoadMode.Merge; j.KeyColumns = "Id"; j.TargetSchema = "mrg";
            configure?.Invoke(j);
        }));

    private async Task<(long Ins, long Upd, long Del, string Status)> LastRunAsync(ExtractJobDef job)
    {
        await using var c = fx.Db(Meta);
        return await c.QuerySingleAsync<(long, long, long, string)>(
            "SELECT TOP 1 ISNULL(RowsInserted, -1), ISNULL(RowsUpdated, -1), ISNULL(RowsDeleted, -1), Status FROM etl.LoadRun WHERE ExtractJobId = @id ORDER BY LoadRunId DESC",
            new { id = job.ExtractJobId });
    }

    private Task<int> CountAsync(string sql) => fx.ScalarAsync<int>(Dwh, sql);

    [Fact]
    public async Task Merge_InsertsNewKeys_UpdatesOnlyChangedRows_AndKeepsOneRowPerKey()
    {
        var t = await SourceAsync("M_Upsert");
        var job = await MergeJobAsync(t);

        await fx.RunExtractAsync(job);
        Assert.Equal((3L, 0L, 0L, "Succeeded"), await LastRunAsync(job));
        Assert.Equal(3, await CountAsync($"SELECT COUNT(*) FROM mrg.{t}"));
        Assert.Equal(1, await CountAsync($"SELECT COUNT(*) FROM sys.indexes WHERE object_id = OBJECT_ID('mrg.{t}') AND name = 'UX_mrg_{t}_Key' AND is_unique = 1"));
        var firstLoad = await fx.ScalarAsync<long>(Dwh, $"SELECT MAX(_LoadId) FROM mrg.{t}");

        // nothing changed (NULLs, xml and decimal(38) compare equal): no row is rewritten
        await fx.RunExtractAsync(job);
        Assert.Equal((0L, 0L, 0L, "Succeeded"), await LastRunAsync(job));
        Assert.Equal(firstLoad, await fx.ScalarAsync<long>(Dwh, $"SELECT MAX(_LoadId) FROM mrg.{t}"));

        await fx.ExecAsync(Src, $"""
            UPDATE dbo.{t} SET Name = N'Bora B' WHERE Id = 2;
            UPDATE dbo.{t} SET Note = N'now set' WHERE Id = 3;              -- NULL -> value
            UPDATE dbo.{t} SET Doc = N'<a x="2"/>' WHERE Id = 1;             -- xml change
            INSERT dbo.{t} VALUES (4, N'Deniz', 4, NULL, NULL, '2026-01-02T10:00:00');
            """);
        await fx.RunExtractAsync(job);
        Assert.Equal((1L, 3L, 0L, "Succeeded"), await LastRunAsync(job));
        Assert.Equal(4, await CountAsync($"SELECT COUNT(*) FROM mrg.{t}"));
        Assert.Equal("Bora B", await fx.ScalarAsync<string>(Dwh, $"SELECT Name FROM mrg.{t} WHERE Id = 2"));
        Assert.Equal("12345678901234567890.1234567890", await fx.ScalarAsync<string>(Dwh, $"SELECT CONVERT(varchar(50), Amount) FROM mrg.{t} WHERE Id = 1"));
        var lastLoad = await fx.ScalarAsync<long>(Meta, "SELECT MAX(LoadRunId) FROM etl.LoadRun WHERE ExtractJobId = @id", new { id = job.ExtractJobId });
        Assert.Equal(4, await CountAsync($"SELECT COUNT(*) FROM mrg.{t} WHERE _LoadId = {lastLoad}"));
    }

    [Fact]
    public async Task Merge_DeleteMissing_RemovesVanishedKeys_ButNeverEmptiesOnAnEmptyRead()
    {
        var t = await SourceAsync("M_Delete");
        var job = await MergeJobAsync(t, j => j.MergeDeleteMissing = true);
        await fx.RunExtractAsync(job);

        await fx.ExecAsync(Src, $"DELETE dbo.{t} WHERE Id = 2;");
        await fx.RunExtractAsync(job);
        Assert.Equal((0L, 0L, 1L, "Succeeded"), await LastRunAsync(job));
        Assert.Equal(0, await CountAsync($"SELECT COUNT(*) FROM mrg.{t} WHERE Id = 2"));
        Assert.Equal(2, await CountAsync($"SELECT COUNT(*) FROM mrg.{t}"));

        await fx.ExecAsync(Src, $"DELETE dbo.{t};");
        await fx.RunExtractAsync(job);
        Assert.Equal((0L, 0L, 0L, "Succeeded"), await LastRunAsync(job));
        Assert.Equal(2, await CountAsync($"SELECT COUNT(*) FROM mrg.{t}"));
    }

    [Fact]
    public async Task Merge_WithWatermark_ReadsOnlyChangedRows()
    {
        var t = await SourceAsync("M_Watermark");
        var job = await MergeJobAsync(t, j => j.WatermarkColumn = "ModifiedAt");
        await fx.RunExtractAsync(job);
        Assert.Equal((3L, 0L, 0L, "Succeeded"), await LastRunAsync(job));

        await fx.ExecAsync(Src, $"UPDATE dbo.{t} SET Name = N'Cem C', ModifiedAt = '2026-01-03T10:00:00' WHERE Id = 3;");
        await fx.RunExtractAsync(job);
        Assert.Equal(1L, await fx.ScalarAsync<long>(Meta,
            "SELECT TOP 1 RowsLoaded FROM etl.LoadRun WHERE ExtractJobId = @id ORDER BY LoadRunId DESC", new { id = job.ExtractJobId }));
        Assert.Equal((0L, 1L, 0L, "Succeeded"), await LastRunAsync(job));
        Assert.Equal(3, await CountAsync($"SELECT COUNT(*) FROM mrg.{t}"));
        Assert.Equal("Cem C", await fx.ScalarAsync<string>(Dwh, $"SELECT Name FROM mrg.{t} WHERE Id = 3"));
    }

    [Fact]
    public async Task DuplicateSourceKeys_FailTheRun_AndLeaveTheTargetUnchanged()
    {
        var t = await SourceAsync("M_Dup");
        var job = await MergeJobAsync(t);
        await fx.RunExtractAsync(job);

        job.SourceType = SourceType.Query; job.SourceTable = null;
        job.SourceQuery = $"SELECT Id, Name, Amount, Doc, Note, ModifiedAt FROM dbo.{t} UNION ALL SELECT 2, N'again', NULL, NULL, NULL, GETDATE()";
        await fx.Repo.SaveExtractJobAsync(job, "tests");
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => fx.RunExtractAsync(job));
        Assert.Contains("same key", ex.Message);
        Assert.Contains("(2)", ex.Message);

        Assert.Equal("Failed", (await LastRunAsync(job)).Status);
        Assert.Equal(3, await CountAsync($"SELECT COUNT(*) FROM mrg.{t}"));
        Assert.Equal("Bora", await fx.ScalarAsync<string>(Dwh, $"SELECT Name FROM mrg.{t} WHERE Id = 2"));
    }

    [Fact]
    public async Task ExistingTableWithDuplicateKeys_IsRefusedWithAClearMessage()
    {
        var t = await SourceAsync("M_Existing");
        var append = await fx.SaveAsync(fx.NewExtractJob(t, j => { j.SourceTable = t; j.TargetSchema = "mrg"; }));
        await fx.RunExtractAsync(append);
        await fx.RunExtractAsync(append);   // every key twice
        await fx.Repo.DeleteExtractJobAsync(append.ExtractJobId, "tests");

        var job = await MergeJobAsync(t);
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => fx.RunExtractAsync(job));
        Assert.Contains("already contains rows with the same key", ex.Message);
        Assert.Equal(6, await CountAsync($"SELECT COUNT(*) FROM mrg.{t}"));
    }

    [Fact]
    public async Task IncrementalExport_AfterMerge_SendsOnlyChangedRows()
    {
        var t = await SourceAsync("M_Export");
        var job = await MergeJobAsync(t, j => j.TargetSchema = "mrgexp");
        await fx.RunExtractAsync(job);

        var export = new ExportJobDef
        {
            Name = "mrg_" + Guid.NewGuid().ToString("N")[..6], ExportMode = ExportMode.AllTables,
            SourceServerId = fx.TargetServerId, SourceDatabase = Dwh, SourceSchema = "mrgexp",
            Bucket = CollectorFixture.Bucket, ObjectNameTemplate = "merge-exports/{Schema}.{Table}/{DateCompact}_{Time}_{Part}.csv",
            CronExpression = "0 1 * * *", TimeZoneId = "UTC", UploadWhenEmpty = false, WriteManifest = false, CommandTimeoutSec = 300
        };
        await fx.Repo.SaveExportJobAsync(export, "tests");
        try
        {
            await fx.RunExportAsync(export);
            Assert.Equal(3L, await fx.ScalarAsync<long>(Meta,
                "SELECT TOP 1 RowsExported FROM etl.ExportRun WHERE ExportJobId = @id ORDER BY ExportRunId DESC", new { id = export.ExportJobId }));

            await fx.ExecAsync(Src, $"UPDATE dbo.{t} SET Name = N'Bora 2' WHERE Id = 2;");
            await fx.RunExtractAsync(job);
            await fx.RunExportAsync(export);
            Assert.Equal(1L, await fx.ScalarAsync<long>(Meta,
                "SELECT TOP 1 RowsExported FROM etl.ExportRun WHERE ExportJobId = @id ORDER BY ExportRunId DESC", new { id = export.ExportJobId }));
        }
        finally { await fx.Repo.DeleteExportJobAsync(export.ExportJobId, "tests"); }
    }
}
