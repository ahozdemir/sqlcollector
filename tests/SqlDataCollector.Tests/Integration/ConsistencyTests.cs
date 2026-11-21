using Dapper;
using SqlDataCollector.Infrastructure;
using SqlDataCollector.Models;
using SqlDataCollector.Quality;

namespace SqlDataCollector.Tests.Integration;

/// <summary>Consistent reads (SNAPSHOT) and a source count that ignores rows added after the read.</summary>
[Collection(IntegrationCollection.Name)]
[Trait("Category", "Integration")]
public sealed class ConsistencyTests(CollectorFixture fx)
{
    private const string Meta = "DataCollectorMeta";

    [Fact]
    public async Task SnapshotRead_Works_WhenAllowed_AndExplainsTheDbaCommandWhenNot()
    {
        await fx.ExecAsync("master", "ALTER DATABASE SrcDb SET ALLOW_SNAPSHOT_ISOLATION ON;");
        var ok = await fx.SaveAsync(fx.NewExtractJob("T_Snapshot", j =>
        {
            j.SourceTable = "Customers"; j.LoadMode = LoadMode.Replace; j.TargetSchema = "cons"; j.ReadSnapshot = true; j.UseNoLock = true;
        }));
        await fx.RunExtractAsync(ok);
        Assert.Equal(3, await fx.ScalarAsync<int>("DWH", "SELECT COUNT(*) FROM cons.T_Snapshot"));

        // the metadata database does not allow snapshot reads
        var refused = await fx.SaveAsync(fx.NewExtractJob("T_NoSnapshot", j =>
        {
            j.SourceDatabase = Meta; j.SourceSchema = "etl"; j.SourceTable = "SqlServer"; j.LoadMode = LoadMode.Replace; j.TargetSchema = "cons"; j.ReadSnapshot = true;
        }));
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => fx.RunExtractAsync(refused));
        Assert.Contains("ALLOW_SNAPSHOT_ISOLATION ON", ex.Message);
        Assert.Contains($"[{Meta}]", ex.Message);

        await fx.Repo.DeleteExtractJobAsync(ok.ExtractJobId, "tests");
        await fx.Repo.DeleteExtractJobAsync(refused.ExtractJobId, "tests");
    }

    [Fact]
    public async Task SourceCount_WithCutoff_IgnoresRowsAddedAfterTheRead()
    {
        await fx.ExecAsync("SrcDb", """
            DROP TABLE IF EXISTS dbo.C_Growing;
            CREATE TABLE dbo.C_Growing(OrderId int IDENTITY(1,1) PRIMARY KEY, Amount decimal(18,2) NOT NULL);
            INSERT dbo.C_Growing (Amount) VALUES (1), (2), (3), (4), (5);
            """);
        var job = await fx.SaveAsync(fx.NewExtractJob("C_Growing", j => { j.SourceTable = "C_Growing"; j.LoadMode = LoadMode.Replace; j.TargetSchema = "cons"; }));
        var withCutoff = await fx.Repo.SaveQualityCheckAsync(new QualityCheckDef
            { ExtractJobId = job.ExtractJobId, CheckType = QualityCheckTypes.SourceCount, Threshold = 0, ColumnName = "OrderId" }, "tests");
        var plain = await fx.Repo.SaveQualityCheckAsync(new QualityCheckDef
            { ExtractJobId = job.ExtractJobId, CheckType = QualityCheckTypes.SourceCount, Threshold = 0, Severity = "Warning" }, "tests");
        try
        {
            await fx.RunExtractAsync(job);
            var loadId = await fx.ScalarAsync<long>(Meta, "SELECT MAX(LoadRunId) FROM etl.LoadRun WHERE ExtractJobId = @id", new { id = job.ExtractJobId });
            Assert.Equal("Passed", await fx.ScalarAsync<string>(Meta, "SELECT QualityStatus FROM etl.LoadRun WHERE LoadRunId = @loadId", new { loadId }));

            // the source keeps working: 3 new orders after the read; the checks run again on the same load
            await fx.ExecAsync("SrcDb", "INSERT dbo.C_Growing (Amount) VALUES (6), (7), (8);");
            var source = (await fx.Repo.GetServerAsync(fx.SourceServerId))!;
            var target = (await fx.Repo.GetServerAsync(fx.TargetServerId))!;
            await using (var targetConn = await fx.Get<SqlConnectionFactory>().OpenAsync(target, "DWH", forWrite: true, CancellationToken.None))
                await fx.Get<QualityService>().RunAsync((await fx.Repo.GetExtractJobAsync(job.ExtractJobId))!, loadId, 5, source, target, targetConn, CancellationToken.None);

            await using var c = fx.Db(Meta);
            var latest = (await c.QueryAsync<(int CheckId, string Status, string Message)>("""
                SELECT QualityCheckId, Status, Message FROM etl.QualityResult r
                WHERE QualityResultId IN (SELECT MAX(QualityResultId) FROM etl.QualityResult WHERE LoadRunId = @loadId GROUP BY QualityCheckId);
                """, new { loadId })).ToDictionary(x => x.CheckId);
            Assert.Equal("Passed", latest[withCutoff].Status);                  // counts orders up to OrderId 5 only
            Assert.Contains("with OrderId <= 5", latest[withCutoff].Message);
            Assert.Equal("Warning", latest[plain].Status);                      // a plain recount sees 8 rows
            Assert.Contains("Source 8 row(s)", latest[plain].Message);
        }
        finally { await fx.Repo.DeleteExtractJobAsync(job.ExtractJobId, "tests"); }
    }
}
