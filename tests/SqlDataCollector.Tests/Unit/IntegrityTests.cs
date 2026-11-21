using System.IO.Compression;
using System.Text;
using SqlDataCollector.Export;
using SqlDataCollector.Models;
using SqlDataCollector.Quality;

namespace SqlDataCollector.Tests.Unit;

public class IntegrityTests
{
    [Theory]
    [InlineData("h1,h2\n", 1)]
    [InlineData("h1,h2\n1,a\n2,b\n", 3)]
    [InlineData("h1,h2\r\n1,\"line\r\nbreak\"\r\n2,\"say \"\"hi\"\"\"\r\n", 3)]   // CR/LF inside quotes, doubled quotes
    [InlineData("h1,h2\n1,\"a,\nb\"\n2,x", 3)]                                     // last record without a final line break
    [InlineData("", 0)]
    public async Task CsvRecords_AreCountedOutsideQuotes(string csv, long expected) =>
        Assert.Equal(expected, await FileRowCounter.CountRecordsAsync(new StringReader(csv), CancellationToken.None));

    [Fact]
    public async Task CsvFile_RowsExcludeTheHeader_AndGzipIsRead()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sdc-int-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var plain = Path.Combine(dir, "a.csv");
            await File.WriteAllTextAsync(plain, "﻿Id,Note\n1,\"x\ny\"\n2,z\n", new UTF8Encoding(false));
            Assert.Equal(2, await FileRowCounter.CountCsvAsync(plain, CancellationToken.None));

            var gz = Path.Combine(dir, "b.csv.gz");
            await using (var f = File.Create(gz))
            await using (var z = new GZipStream(f, CompressionLevel.Fastest))
                await z.WriteAsync(Encoding.UTF8.GetBytes("Id\n1\n2\n3\n"));
            Assert.Equal(3, await FileRowCounter.CountCsvAsync(gz, CancellationToken.None));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void SourceCount_CutoffAndSnapshot()
    {
        var job = new ExtractJobDef { SourceSchema = "dbo", SourceTable = "Orders", FilterClause = "Status <> 'D'", UseNoLock = true };
        Assert.Equal("SELECT COUNT_BIG(*) FROM [dbo].[Orders] WITH (NOLOCK) WHERE (Status <> 'D') AND [OrderId] <= @cutoff",
            QualityRules.SourceCountSql(job, "OrderId"));
        job.ReadSnapshot = true;   // a snapshot read ignores NOLOCK
        Assert.Equal("SELECT COUNT_BIG(*) FROM [dbo].[Orders] WHERE (Status <> 'D')", QualityRules.SourceCountSql(job));
    }
}
