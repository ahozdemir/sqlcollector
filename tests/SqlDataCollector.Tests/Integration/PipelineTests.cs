using System.IO.Compression;
using System.Text;
using SqlDataCollector.Export;
using SqlDataCollector.Models;
using SqlDataCollector.Scheduling;

namespace SqlDataCollector.Tests.Integration;

/// <summary>End-to-end tests of extract, retention and export against SQL Server + fake GCS in Docker.</summary>
[Collection(IntegrationCollection.Name)]
[Trait("Category", "Integration")]
public sealed class PipelineTests(CollectorFixture fx)
{
    private const string Dwh = "DWH";

    [Fact]
    public async Task Extract_Append_CreatesTargetWithTechnicalColumns_AndAppendsOnEachRun()
    {
        var job = await fx.SaveAsync(fx.NewExtractJob("T_Append"));

        await fx.RunExtractAsync(job);
        await fx.RunExtractAsync(job);

        Assert.Equal(8, await fx.ScalarAsync<int>(Dwh, "SELECT COUNT(*) FROM stage.T_Append"));
        Assert.Equal(2, await fx.ScalarAsync<int>(Dwh, "SELECT COUNT(DISTINCT _LoadId) FROM stage.T_Append"));
        Assert.Equal(job.ExtractJobId, await fx.ScalarAsync<int>(Dwh, "SELECT MAX(_ExtractJobId) FROM stage.T_Append"));
        Assert.Equal(CollectorFixture.SourceServerName, await fx.ScalarAsync<string>(Dwh, "SELECT MAX(_SourceServer) FROM stage.T_Append"));
        // rowversion lands as binary(8) because it cannot be inserted into a rowversion column
        Assert.Equal("binary", await fx.ScalarAsync<string>(Dwh,
            "SELECT t.name FROM sys.columns c JOIN sys.types t ON t.user_type_id = c.user_type_id WHERE c.object_id = OBJECT_ID('stage.T_Append') AND c.name = 'Rv'"));
    }

    [Fact]
    public async Task Extract_Decimal38_And_UnicodeText_ArePreservedExactly()
    {
        var job = await fx.SaveAsync(fx.NewExtractJob("T_Exact"));
        await fx.RunExtractAsync(job);

        Assert.Equal("1234567890123456789012345678.1234567890",
            await fx.ScalarAsync<string>(Dwh, "SELECT CONVERT(varchar(60), Amount) FROM stage.T_Exact WHERE CustomerName = N'Şükrü Öztürk'"));
        Assert.Equal(1, await fx.ScalarAsync<int>(Dwh, "SELECT COUNT(*) FROM stage.T_Exact WHERE CustomerName = N'Çağla İğdır'"));
        Assert.Equal(1, await fx.ScalarAsync<int>(Dwh, "SELECT COUNT(*) FROM stage.T_Exact WHERE CustomerName = N''")); // empty string stays empty, not NULL
    }

    [Fact]
    public async Task Extract_Incremental_DatetimeWatermark_DoesNotReloadRows()
    {
        // dedicated source table so other tests are not affected
        await fx.ExecAsync("SrcDb", """
            IF OBJECT_ID('dbo.Events') IS NOT NULL DROP TABLE dbo.Events;
            CREATE TABLE dbo.Events (Id int PRIMARY KEY, Payload nvarchar(50), CreatedAt datetime NOT NULL);
            INSERT dbo.Events VALUES (1, N'a', '2026-11-20 10:00:00.003'), (2, N'b', '2026-11-20 13:00:00.997');
            """);
        var job = await fx.SaveAsync(fx.NewExtractJob("T_Incr", j =>
        {
            j.SourceType = SourceType.Query; j.SourceTable = null;
            j.SourceQuery = ";WITH x AS (SELECT * FROM dbo.Events WHERE (@WatermarkFrom IS NULL OR CreatedAt > @WatermarkFrom)) SELECT * FROM x";
            j.LoadMode = LoadMode.Incremental; j.WatermarkColumn = "CreatedAt";
        }));

        await fx.RunExtractAsync(job);
        await fx.RunExtractAsync(job); // .997 must not be re-read (datetime vs datetime2 parameter trap)
        Assert.Equal(2, await fx.ScalarAsync<int>(Dwh, "SELECT COUNT(*) FROM stage.T_Incr"));

        await fx.ExecAsync("SrcDb", "INSERT dbo.Events VALUES (3, N'c', '2026-11-21 08:00:00')");
        await fx.RunExtractAsync(job);
        Assert.Equal(3, await fx.ScalarAsync<int>(Dwh, "SELECT COUNT(*) FROM stage.T_Incr"));

        var state = await fx.Repo.GetExtractStateAsync(job.ExtractJobId);
        Assert.Equal("datetime", state!.WatermarkType);
    }

    [Fact]
    public async Task Extract_Incremental_Rowversion()
    {
        var job = await fx.SaveAsync(fx.NewExtractJob("T_Rv", j =>
        {
            j.ColumnList = "OrderId, Rv"; j.LoadMode = LoadMode.Incremental; j.WatermarkColumn = "Rv";
        }));
        await fx.RunExtractAsync(job);
        await fx.RunExtractAsync(job);
        Assert.Equal(4, await fx.ScalarAsync<int>(Dwh, "SELECT COUNT(*) FROM stage.T_Rv"));
    }

    [Fact]
    public async Task Extract_Replace_KeepsOnlyTheLatestLoad()
    {
        var job = await fx.SaveAsync(fx.NewExtractJob("T_Replace", j =>
        {
            j.SourceTable = "Customers"; j.LoadMode = LoadMode.Replace;
        }));
        await fx.RunExtractAsync(job);
        await fx.RunExtractAsync(job);

        Assert.Equal(3, await fx.ScalarAsync<int>(Dwh, "SELECT COUNT(*) FROM stage.T_Replace"));
        Assert.Equal(1, await fx.ScalarAsync<int>(Dwh, "SELECT COUNT(DISTINCT _LoadId) FROM stage.T_Replace"));
    }

    [Fact]
    public async Task Extract_StoredProcedureWithTempTable_Works()
    {
        var job = await fx.SaveAsync(fx.NewExtractJob("T_Proc", j =>
        {
            j.SourceType = SourceType.Query; j.SourceTable = null; j.SourceQuery = "EXEC dbo.usp_CitySummary";
        }));
        await fx.RunExtractAsync(job);
        Assert.Equal(3, await fx.ScalarAsync<int>(Dwh, "SELECT COUNT(*) FROM stage.T_Proc"));
    }

    [Fact]
    public async Task Extract_Strict_FailsOnMismatch_RollsBack_AndRecordsFailure()
    {
        await fx.ExecAsync(Dwh, "IF OBJECT_ID('stage.T_Strict') IS NOT NULL DROP TABLE stage.T_Strict; CREATE TABLE stage.T_Strict (CustomerId int, Name nvarchar(10));");
        var job = await fx.SaveAsync(fx.NewExtractJob("T_Strict", j => j.SourceTable = "Customers"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => fx.RunExtractAsync(job));
        Assert.Contains("Strict schema policy", ex.Message);
        Assert.Equal(0, await fx.ScalarAsync<int>(Dwh, "SELECT COUNT(*) FROM stage.T_Strict"));
        Assert.Equal("Failed", await fx.ScalarAsync<string>("DataCollectorMeta",
            "SELECT TOP 1 Status FROM etl.LoadRun WHERE ExtractJobId = @id ORDER BY LoadRunId DESC", new { id = job.ExtractJobId }));
    }

    [Fact]
    public async Task Extract_Evolve_AddsTechnicalAndMissingColumns_AndWidensText()
    {
        await fx.ExecAsync(Dwh, "IF OBJECT_ID('stage.T_Evolve') IS NOT NULL DROP TABLE stage.T_Evolve; CREATE TABLE stage.T_Evolve (CustomerId int, Name nvarchar(10));");
        var job = await fx.SaveAsync(fx.NewExtractJob("T_Evolve", j => { j.SourceTable = "Customers"; j.SchemaPolicy = SchemaPolicy.Evolve; }));

        await fx.RunExtractAsync(job);

        Assert.Equal(3, await fx.ScalarAsync<int>(Dwh, "SELECT COUNT(*) FROM stage.T_Evolve WHERE City IS NOT NULL AND _LoadId IS NOT NULL"));
        Assert.Equal(100, await fx.ScalarAsync<int>(Dwh, "SELECT max_length FROM sys.columns WHERE object_id = OBJECT_ID('stage.T_Evolve') AND name = 'Name'")); // nvarchar(50)
    }

    [Fact]
    public async Task Retention_OnDailyPartitionedTable_TruncatesWholeOldDays()
    {
        var job = await fx.SaveAsync(fx.NewExtractJob("T_Part", j =>
        {
            j.SourceTable = "Customers"; j.PartitionDaily = true; j.RetentionDays = 1;
        }));
        await fx.RunExtractAsync(job);
        Assert.Equal(1, await fx.ScalarAsync<int>(Dwh, "SELECT COUNT(*) FROM sys.partition_functions WHERE name = 'pf_stage_T_Part_daily'"));

        // simulate rows loaded 5 days ago
        await fx.ExecAsync(Dwh, """
            INSERT stage.T_Part (_LoadId, _LoadedAt, _ExtractJobId, _SourceServer, CustomerId, Name, City)
            VALUES (-1, DATEADD(day, -5, SYSUTCDATETIME()), @job, 'SRC', 99, N'old', N'x');
            """, new { job = job.ExtractJobId });

        await fx.Get<RetentionRunner>().RunAsync(CancellationToken.None);

        Assert.Equal(0, await fx.ScalarAsync<int>(Dwh, "SELECT COUNT(*) FROM stage.T_Part WHERE CustomerId = 99"));
        Assert.Equal(3, await fx.ScalarAsync<int>(Dwh, "SELECT COUNT(*) FROM stage.T_Part"));
        // boundaries stay ahead of today
        Assert.True(await fx.ScalarAsync<DateTime>(Dwh,
            "SELECT MAX(CAST(prv.value AS datetime2)) FROM sys.partition_range_values prv JOIN sys.partition_functions pf ON pf.function_id = prv.function_id WHERE pf.name = 'pf_stage_T_Part_daily'")
            >= DateTime.UtcNow.Date.AddDays(6));
    }

    [Fact]
    public async Task Export_AllTables_UploadsPlainCsvPerTable_ArchivesGzip_AndIsIncremental()
    {
        // extract jobs writing into a dedicated schema, so only these tables are exported
        var orders = await fx.SaveAsync(fx.NewExtractJob("Orders", j => j.TargetSchema = "exp"));
        var customers = await fx.SaveAsync(fx.NewExtractJob("Customers", j => { j.TargetSchema = "exp"; j.SourceTable = "Customers"; }));
        await fx.RunExtractAsync(orders);
        await fx.RunExtractAsync(customers);

        var export = new ExportJobDef
        {
            Name = "all_" + Guid.NewGuid().ToString("N")[..6], ExportMode = ExportMode.AllTables,
            SourceServerId = fx.TargetServerId, SourceDatabase = Dwh, SourceSchema = "exp",
            Bucket = CollectorFixture.Bucket, ObjectNameTemplate = ObjectNameTemplate.DefaultAllTables,
            CronExpression = "0 1 * * *", TimeZoneId = "UTC", UploadWhenEmpty = false, WriteManifest = true, CommandTimeoutSec = 300
        };
        await fx.Repo.SaveExportJobAsync(export, "tests");

        // run 1: both tables, plain CSV in GCS
        await fx.RunExportAsync(export);
        var objects = await fx.ListObjectsAsync("exports/DWH/exp.");
        var ordersCsv = Assert.Single(objects, o => o.Name.StartsWith("exports/DWH/exp.Orders/") && o.Name.EndsWith(".csv"));
        Assert.Single(objects, o => o.Name.StartsWith("exports/DWH/exp.Customers/") && o.Name.EndsWith(".csv"));
        Assert.StartsWith("text/csv", ordersCsv.ContentType);

        var text = await fx.DownloadTextAsync(ordersCsv.Name);
        var rows = Csv.Parse(text);
        Assert.Equal(5, rows.Count);                                   // header + 4
        Assert.All(rows, r => Assert.Equal(rows[0].Length, r.Length));  // no shifted columns
        var note = Array.IndexOf(rows[0], "Note");
        Assert.Contains(rows, r => r[note] == "a,b \"quoted\"");
        Assert.Contains(rows, r => r[note] == "satır1\r\nsatır2");
        Assert.DoesNotContain("_LoadId", rows[0]);                      // technical columns excluded by default
        Assert.Contains((await fx.ListObjectsAsync("exports/DWH/_manifests/")), o => o.Name.Contains(export.Name));

        // local archive: gzip only, identical content
        var archived = Directory.GetFiles(fx.WorkDirectory, "*", SearchOption.AllDirectories)
            .Where(f => f.Contains(export.Name)).ToList();
        Assert.NotEmpty(archived);
        Assert.All(archived, f => Assert.EndsWith(".csv.gz", f));
        var ordersArchive = archived.Single(f => f.Contains("exp.Orders"));
        await using (var gz = new GZipStream(File.OpenRead(ordersArchive), CompressionMode.Decompress))
        using (var reader = new StreamReader(gz, Encoding.UTF8))
            Assert.Equal(text, await reader.ReadToEndAsync());

        // run 2: no new loads -> nothing uploaded
        await fx.RunExportAsync(export);
        Assert.Equal(objects.Count + 1, (await fx.ListObjectsAsync("exports/DWH/exp.")).Count + 1); // unchanged
        Assert.Equal("Skipped", await fx.ScalarAsync<string>("DataCollectorMeta",
            "SELECT TOP 1 Status FROM etl.ExportRun WHERE ExportJobId = @id ORDER BY ExportRunId DESC", new { id = export.ExportJobId }));

        // run 3: one new load of Customers -> only that table, only the new rows
        await fx.RunExtractAsync(customers);
        await Task.Delay(1100); // object names contain seconds
        await fx.RunExportAsync(export);
        var after = await fx.ListObjectsAsync("exports/DWH/exp.Customers/");
        Assert.Equal(2, after.Count);
        Assert.Equal(4, Csv.Parse(await fx.DownloadTextAsync(after[^1].Name)).Count); // header + 3 new rows
        Assert.Single(await fx.ListObjectsAsync("exports/DWH/exp.Orders/"));
    }
}

/// <summary>Minimal RFC 4180 parser for assertions.</summary>
internal static class Csv
{
    public static List<string[]> Parse(string text)
    {
        var rows = new List<string[]>();
        var row = new List<string>();
        var field = new StringBuilder();
        bool quoted = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else field.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == ',') { row.Add(field.ToString()); field.Clear(); }
            else if (c == '\n') { row.Add(field.ToString()); field.Clear(); rows.Add(row.ToArray()); row.Clear(); }
            else if (c != '\r') field.Append(c);
        }
        if (field.Length > 0 || row.Count > 0) { row.Add(field.ToString()); rows.Add(row.ToArray()); }
        return rows;
    }
}
