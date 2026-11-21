using System.Numerics;
using Dapper;
using Parquet;
using Parquet.Data;
using Parquet.Schema;
using Parquet.Serialization;
using SqlDataCollector.Models;
using SqlDataCollector.Privacy;

namespace SqlDataCollector.Tests.Integration;

/// <summary>Parquet export: typed columns read back with a Parquet reader, file splitting, masking.</summary>
[Collection(IntegrationCollection.Name)]
[Trait("Category", "Integration")]
public sealed class ParquetExportTests(CollectorFixture fx)
{
    private const string Dwh = "DWH";
    private static readonly ParquetOptions ReadOptions = new() { UseBigDecimal = true };

    public sealed class TypesRow
    {
        public int? Id { get; set; }
        public bool? Flag { get; set; }
        public int? Tiny { get; set; }
        public int? Small { get; set; }
        public long? Big { get; set; }
        public BigDecimal? Amount { get; set; }
        public BigDecimal? Huge { get; set; }
        public BigDecimal? Price { get; set; }
        public double? F { get; set; }
        public float? R { get; set; }
        public DateTime? D { get; set; }
        public DateTime? DT { get; set; }
        public DateTime? DT2 { get; set; }
        public DateTime? DTO { get; set; }
        public string? T { get; set; }
        public string? G { get; set; }
        public string? Txt { get; set; }
        public byte[]? Bin { get; set; }
    }

    public sealed class ManyRow
    {
        public int? Id { get; set; }
        public string? Email { get; set; }
        public BigDecimal? Amount { get; set; }
    }

    private async Task<ExportJobDef> ExportAsync(string table, string prefix, Action<ExportJobDef>? configure = null)
    {
        var e = new ExportJobDef
        {
            Name = "pq_" + Guid.NewGuid().ToString("N")[..6], ExportMode = ExportMode.Full, FileFormat = ExportFileFormat.Parquet,
            SourceServerId = fx.TargetServerId, SourceDatabase = Dwh, SourceSchema = "pq", SourceTable = table,
            Bucket = CollectorFixture.Bucket, ObjectNameTemplate = prefix + "/{Name}_{Part}.csv",   // .csv in the template becomes .parquet
            CronExpression = "0 1 * * *", TimeZoneId = "UTC", UploadWhenEmpty = false, WriteManifest = false, CommandTimeoutSec = 300
        };
        configure?.Invoke(e);
        await fx.Repo.SaveExportJobAsync(e, "tests");
        return e;
    }

    [Fact]
    public async Task Parquet_KeepsTypes_ExactDecimal38_Microseconds_AndNulls()
    {
        await fx.ExecAsync(Dwh, """
            IF SCHEMA_ID('pq') IS NULL EXEC('CREATE SCHEMA pq');
            DROP TABLE IF EXISTS pq.Types;
            CREATE TABLE pq.Types(Id int NOT NULL, Flag bit, Tiny tinyint, Small smallint, Big bigint, Amount decimal(18,2), Huge decimal(38,10),
                Price money, F float, R real, D date, DT datetime, DT2 datetime2(7), DTO datetimeoffset(3), T time(3),
                G uniqueidentifier, Txt nvarchar(50), Bin varbinary(10));
            INSERT pq.Types VALUES
              (1, 1, 255, -32768, 9223372036854775807, -1234.56, 12345678901234567890.1234567890, 922337203685477.5807, 1.5, 2.25,
               '2026-11-21', '2026-11-21T10:20:30.997', '2026-11-21T10:20:30.1234567', '2026-11-21T10:20:30.123+03:00', '10:20:30.123',
               '3F2504E0-4F89-11D3-9A0C-0305E82C3301', N'Ayşe Çağ, "quoted"', 0x0102FF),
              (2, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL);
            """);
        var e = await ExportAsync("Types", "pq-types");
        await fx.RunExportAsync(e);

        var obj = Assert.Single(await fx.ListObjectsAsync($"pq-types/{e.Name}"));
        Assert.EndsWith("_0001.parquet", obj.Name);
        var bytes = await fx.DownloadBytesAsync(obj.Name);

        await using (var reader = await ParquetReader.CreateAsync(new MemoryStream(bytes), ReadOptions))
        {
            var huge = Assert.IsType<DecimalDataField>(reader.Schema.DataFields.Single(f => f.Name == "Huge"), exactMatch: false);
            Assert.Equal((38, 10), (huge.Precision, huge.Scale));
            Assert.IsType<DateTimeDataField>(reader.Schema.DataFields.Single(f => f.Name == "DT2"), exactMatch: false);
        }

        var rows = (await ParquetSerializer.DeserializeAsync<TypesRow>(new MemoryStream(bytes), ReadOptions)).Data.OrderBy(r => r.Id).ToList();
        Assert.Equal(2, rows.Count);
        var r = rows[0];
        Assert.True(r.Flag);
        Assert.Equal((255, -32768, long.MaxValue), (r.Tiny!.Value, r.Small!.Value, r.Big!.Value));
        Assert.Equal(BigInteger.Parse("-123456"), r.Amount!.Value.UnscaledValue);
        Assert.Equal(BigInteger.Parse("123456789012345678901234567890"), r.Huge!.Value.UnscaledValue);   // 30 digits: beyond .NET decimal, exact
        Assert.Equal(10, r.Huge.Value.Scale);
        Assert.Equal(BigInteger.Parse("9223372036854775807"), r.Price!.Value.UnscaledValue);
        Assert.Equal((1.5, 2.25f), (r.F!.Value, r.R!.Value));
        Assert.Equal(new DateTime(2026, 11, 21), r.D!.Value.Date);
        Assert.Equal(new DateTime(2026, 11, 21, 10, 20, 30, 997), r.DT);
        Assert.Equal(new DateTime(2026, 11, 21, 10, 20, 30).AddTicks(1234560), r.DT2);   // microseconds kept, 100 ns dropped
        Assert.Equal(new DateTime(2026, 11, 21, 7, 20, 30, 123), r.DTO);                   // converted to UTC
        Assert.Equal("10:20:30.123", r.T);   // time is written as text
        Assert.Equal("3f2504e0-4f89-11d3-9a0c-0305e82c3301", r.G);
        Assert.Equal("Ayşe Çağ, \"quoted\"", r.Txt);
        Assert.Equal(new byte[] { 1, 2, 255 }, r.Bin);

        var empty = rows[1];
        Assert.True(empty.Flag is null && empty.Huge is null && empty.DT2 is null && empty.Txt is null && empty.Bin is null && empty.T is null);
    }

    [Fact]
    public async Task Parquet_SplitsByMaxRows_MasksColumns_AndCountsRows()
    {
        await fx.ExecAsync(Dwh, """
            IF SCHEMA_ID('pq') IS NULL EXEC('CREATE SCHEMA pq');
            DROP TABLE IF EXISTS pq.Many;
            CREATE TABLE pq.Many(Id int NOT NULL, Email nvarchar(100), Amount decimal(10,2));
            INSERT pq.Many SELECT TOP (2500) n, CONCAT(N'user', n, N'@corp.com'), n / 100.0
            FROM (SELECT ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS n FROM sys.all_columns a CROSS JOIN sys.all_columns b) x;
            """);
        var rule = await fx.Repo.SaveMaskingRuleAsync(new MaskingRule
        {
            Name = "pq_mail_" + Guid.NewGuid().ToString("N")[..6], ColumnPattern = "Email", ScopePattern = "DWH.pq.Many", Method = MaskMethod.Email
        }, "tests");
        try
        {
            var e = await ExportAsync("Many", "pq-many", j => { j.MaxRowsPerFile = 1000; j.ParquetCompression = "Zstd"; });
            await fx.RunExportAsync(e);

            var objects = (await fx.ListObjectsAsync($"pq-many/{e.Name}")).OrderBy(o => o.Name).ToList();
            Assert.Equal(3, objects.Count);
            var counts = new List<int>();
            var ids = new List<int>();
            foreach (var o in objects)
            {
                var rows = (await ParquetSerializer.DeserializeAsync<ManyRow>(new MemoryStream(await fx.DownloadBytesAsync(o.Name)), ReadOptions)).Data;
                counts.Add(rows.Count);
                ids.AddRange(rows.Select(x => x.Id!.Value));
                Assert.All(rows, x => Assert.Matches(@"^u\*\*\*@corp\.com$", x.Email));
            }
            Assert.Equal(new[] { 1000, 1000, 500 }, counts);
            Assert.Equal(2500, ids.Distinct().Count());

            await using var c = fx.Db("DataCollectorMeta");
            var run = await c.QuerySingleAsync<(string Status, long Rows, int Files)>(
                "SELECT TOP 1 Status, RowsExported, FileCount FROM etl.ExportRun WHERE ExportJobId = @id ORDER BY ExportRunId DESC", new { id = e.ExportJobId });
            Assert.Equal(("Succeeded", 2500L, 3), (run.Status, run.Rows, run.Files));
        }
        finally { await fx.Repo.DeleteMaskingRuleAsync(rule, "tests"); }
    }
}
