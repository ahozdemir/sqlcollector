using System.Globalization;
using System.IO.Compression;
using System.Text;
using SqlDataCollector.Export;
using SqlDataCollector.Models;

namespace SqlDataCollector.Tests.Integration;

/// <summary>Local export archive on the Worker: a full export run twice on one day keeps one copy, incremental runs add up.</summary>
[Collection(IntegrationCollection.Name)]
[Trait("Category", "Integration")]
public sealed class ExportArchiveIntegrationTests(CollectorFixture fx)
{
    private string ArchiveOf(ExportJobDef job, params string[] sub) =>
        Path.Combine([fx.WorkDirectory, DateTime.UtcNow.ToString(ExportArchive.DayFormat, CultureInfo.InvariantCulture), ObjectNameTemplate.Sanitize(job.Name), .. sub]);

    private static async Task<string> GunzipAsync(string path)
    {
        await using var gz = new GZipStream(File.OpenRead(path), CompressionMode.Decompress);
        using var reader = new StreamReader(gz, Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }

    private async Task<ExportJobDef> ExportAsync(string name, ExportMode mode, string schema, string table)
    {
        var e = new ExportJobDef
        {
            Name = $"{name}_{Guid.NewGuid().ToString("N")[..6]}", ExportMode = mode, SourceServerId = fx.TargetServerId, SourceDatabase = "DWH",
            SourceSchema = schema, SourceTable = table, Bucket = CollectorFixture.Bucket, ObjectNameTemplate = "archive-tests/{Name}/{Time}_{Part}.csv",
            CronExpression = "0 1 * * *", TimeZoneId = "UTC", UploadWhenEmpty = false, WriteManifest = false, CommandTimeoutSec = 300
        };
        await fx.Repo.SaveExportJobAsync(e, "tests");
        return e;
    }

    [Fact]
    public async Task FullExport_RunAgainOnTheSameDay_ReplacesTheLocalCopy()
    {
        await fx.ExecAsync("DWH", """
            IF SCHEMA_ID('arc') IS NULL EXEC('CREATE SCHEMA arc');
            DROP TABLE IF EXISTS arc.Items;
            CREATE TABLE arc.Items(Id int NOT NULL PRIMARY KEY, Name nvarchar(20) NOT NULL);
            INSERT arc.Items VALUES (1, N'first'), (2, N'first');
            """);
        var export = await ExportAsync("arc_full", ExportMode.Full, "arc", "Items");
        try
        {
            await fx.RunExportAsync(export);
            var first = Assert.Single(Directory.GetFiles(ArchiveOf(export)));
            Assert.Contains("first", await GunzipAsync(first));

            await fx.ExecAsync("DWH", "UPDATE arc.Items SET Name = N'second';");
            await fx.RunExportAsync(export);

            var second = Assert.Single(Directory.GetFiles(ArchiveOf(export)));   // the day's earlier copy was replaced
            var text = await GunzipAsync(second);
            Assert.Contains("second", text);
            Assert.DoesNotContain("first", text);
            Assert.Empty(Directory.GetDirectories(Path.GetDirectoryName(ArchiveOf(export))!, $"{ObjectNameTemplate.Sanitize(export.Name)}_*"));   // run folders removed
        }
        finally { await fx.Repo.DeleteExportJobAsync(export.ExportJobId, "tests"); }
    }

    [Fact]
    public async Task IncrementalExport_RunAgainWithNewLoads_KeepsBothFiles()
    {
        var table = "ArcIncr_" + Guid.NewGuid().ToString("N")[..6];
        await fx.ExecAsync("SrcDb", $"""
            CREATE TABLE dbo.{table}(Id int NOT NULL PRIMARY KEY, Name nvarchar(20) NOT NULL, CreatedAt datetime NOT NULL);
            INSERT dbo.{table} VALUES (1, N'load one', '2026-01-01'), (2, N'load one', '2026-01-01');
            """);
        var extract = await fx.SaveAsync(fx.NewExtractJob(table, j =>
        {
            j.SourceTable = table; j.TargetSchema = "arc"; j.RetentionDays = null;
            j.LoadMode = LoadMode.Incremental; j.WatermarkColumn = "CreatedAt";   // the second load holds only the new row
        }));
        await fx.RunExtractAsync(extract);
        var export = await ExportAsync("arc_incr", ExportMode.Incremental, "arc", table);
        try
        {
            await fx.RunExportAsync(export);
            await fx.ExecAsync("SrcDb", $"INSERT dbo.{table} VALUES (3, N'load two', '2026-01-02');");
            await fx.RunExtractAsync(extract);
            await fx.RunExportAsync(export);

            var files = Directory.GetFiles(ArchiveOf(export)).Order().ToList();
            Assert.Equal(2, files.Count);
            Assert.All(files, f => Assert.StartsWith("run", Path.GetFileName(f)));
            Assert.Contains("load one", await GunzipAsync(files[0]));
            var second = await GunzipAsync(files[1]);
            Assert.Contains("load two", second);
            Assert.DoesNotContain("load one", second);
        }
        finally { await fx.Repo.DeleteExportJobAsync(export.ExportJobId, "tests"); }
    }
}
