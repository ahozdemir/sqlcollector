using Dapper;
using SqlDataCollector.Extract;
using SqlDataCollector.Models;

namespace SqlDataCollector.Tests.Integration;

/// <summary>Merge with SQL Server Change Tracking: only changed keys are read; inserts, updates and deletes reach the target.</summary>
[Collection(IntegrationCollection.Name)]
[Trait("Category", "Integration")]
public sealed class ChangeTrackingTests(CollectorFixture fx)
{
    private const string Src = "SrcDb";
    private const string Dwh = "DWH";
    private const string Meta = "DataCollectorMeta";

    private async Task<string> SourceAsync(string name, bool track = true)
    {
        await fx.ExecAsync("master", """
            IF NOT EXISTS (SELECT 1 FROM sys.change_tracking_databases WHERE database_id = DB_ID('SrcDb'))
                ALTER DATABASE SrcDb SET CHANGE_TRACKING = ON (CHANGE_RETENTION = 2 DAYS, AUTO_CLEANUP = ON);
            """);
        await fx.ExecAsync(Src, $"""
            DROP TABLE IF EXISTS dbo.{name};
            CREATE TABLE dbo.{name}(Id int NOT NULL PRIMARY KEY, Name nvarchar(50) NOT NULL, Qty int NOT NULL, Amount decimal(38,10) NULL);
            INSERT dbo.{name} VALUES (1, N'Ayşe', 5, 12345678901234567890.1234567890), (2, N'Bora', 3, NULL), (3, N'Cem', 1, 1.5), (4, N'Deniz', 2, NULL);
            {(track ? $"ALTER TABLE dbo.{name} ENABLE CHANGE_TRACKING;" : "")}
            """);
        return name;
    }

    private Task<ExtractJobDef> JobAsync(string table, Action<ExtractJobDef>? configure = null) =>
        fx.SaveAsync(fx.NewExtractJob(table, j =>
        {
            j.SourceTable = table; j.LoadMode = LoadMode.Merge; j.UseChangeTracking = true; j.KeyColumns = "Id"; j.TargetSchema = "ctk"; j.RetentionDays = null;
            configure?.Invoke(j);
        }));

    private Task<(long Rows, long Ins, long Upd, long Del, string Status)> LastRunAsync(ExtractJobDef job) =>
        fx.Db(Meta).QuerySingleAsync<(long, long, long, long, string)>("""
            SELECT TOP 1 ISNULL(RowsLoaded, -1), ISNULL(RowsInserted, -1), ISNULL(RowsUpdated, -1), ISNULL(RowsDeleted, -1), Status
            FROM etl.LoadRun WHERE ExtractJobId = @id ORDER BY LoadRunId DESC
            """, new { id = job.ExtractJobId });

    private Task<int> CountAsync(string table) => fx.ScalarAsync<int>(Dwh, $"SELECT COUNT(*) FROM ctk.{table}");

    [Fact]
    public async Task FirstRunReadsAll_ThenOnlyChangedKeys_WithInsertsUpdatesAndDeletes()
    {
        var t = await SourceAsync("CT_Basic");
        var job = await JobAsync(t);

        await fx.RunExtractAsync(job);
        Assert.Equal((4L, 4L, 0L, 0L, "Succeeded"), await LastRunAsync(job));
        var state = await fx.Repo.GetExtractStateAsync(job.ExtractJobId);
        Assert.Equal(ChangeTrackingSql.VersionType, state!.WatermarkType);

        await fx.ExecAsync(Src, $"""
            UPDATE dbo.{t} SET Name = N'Bora B' WHERE Id = 2;
            DELETE dbo.{t} WHERE Id = 3;
            INSERT dbo.{t} VALUES (5, N'Ece', 7, 2.25);
            UPDATE dbo.{t} SET Qty = Qty WHERE Id = 4;      -- tracked, but nothing changed: not rewritten
            """);
        await fx.RunExtractAsync(job);
        Assert.Equal((3L, 1L, 1L, 1L, "Succeeded"), await LastRunAsync(job));   // 3 changed rows read (2, 4, 5), key 3 deleted
        Assert.Equal(4, await CountAsync(t));
        Assert.Equal("Bora B", await fx.ScalarAsync<string>(Dwh, $"SELECT Name FROM ctk.{t} WHERE Id = 2"));
        Assert.Equal(0, await fx.ScalarAsync<int>(Dwh, $"SELECT COUNT(*) FROM ctk.{t} WHERE Id = 3"));
        Assert.Equal("12345678901234567890.1234567890", await fx.ScalarAsync<string>(Dwh, $"SELECT CONVERT(varchar(50), Amount) FROM ctk.{t} WHERE Id = 1"));
        Assert.True(long.Parse((await fx.Repo.GetExtractStateAsync(job.ExtractJobId))!.WatermarkValue!) > long.Parse(state.WatermarkValue!));

        await fx.RunExtractAsync(job);   // nothing changed since
        Assert.Equal((0L, 0L, 0L, 0L, "Succeeded"), await LastRunAsync(job));
    }

    [Fact]
    public async Task RowsLeavingTheFilter_AreDeleted_AndAParallelFullReadWorks()
    {
        var t = await SourceAsync("CT_Filter");
        var job = await JobAsync(t, j => { j.FilterClause = "Qty > 1"; j.ReadParallelism = 2; });
        await fx.RunExtractAsync(job);
        Assert.Equal(3, await CountAsync(t));   // Id 3 (Qty 1) is filtered out

        await fx.ExecAsync(Src, $"UPDATE dbo.{t} SET Qty = 0 WHERE Id = 1; UPDATE dbo.{t} SET Qty = 9 WHERE Id = 3;");
        await fx.RunExtractAsync(job);
        Assert.Equal((1L, 1L, 0L, 1L, "Succeeded"), await LastRunAsync(job));   // 3 enters, 1 leaves
        Assert.Equal(new[] { 2, 3, 4 }, (await fx.Db(Dwh).QueryAsync<int>($"SELECT Id FROM ctk.{t} ORDER BY Id")).ToArray());
    }

    [Fact]
    public async Task VersionOlderThanTheRetention_ReadsTheWholeTableAgain_AndDeletesWhatIsGone()
    {
        // a tracked change elsewhere first, so this table's oldest valid version is above 0
        var other = await SourceAsync("CT_Other");
        await fx.ExecAsync(Src, $"UPDATE dbo.{other} SET Qty = Qty + 1;");
        var t = await SourceAsync("CT_Expired");
        Assert.True(await fx.ScalarAsync<long>(Src, $"SELECT CHANGE_TRACKING_MIN_VALID_VERSION(OBJECT_ID('dbo.{t}'))") > 0);

        var job = await JobAsync(t);
        await fx.RunExtractAsync(job);
        await fx.ExecAsync(Src, $"DELETE dbo.{t} WHERE Id = 1;");
        await fx.ExecAsync(Meta, "UPDATE etl.ExtractState SET WatermarkValue = '0' WHERE ExtractJobId = @id", new { id = job.ExtractJobId });

        await fx.RunExtractAsync(job);
        Assert.Equal((3L, 0L, 0L, 1L, "Succeeded"), await LastRunAsync(job));   // full read of 3 rows, the vanished key deleted
        Assert.Equal(3, await CountAsync(t));
    }

    [Fact]
    public async Task ConsistentSnapshot_ReadsVersionAndChangesAtOnePointInTime()
    {
        await fx.ExecAsync("master", "ALTER DATABASE SrcDb SET ALLOW_SNAPSHOT_ISOLATION ON;");
        var t = await SourceAsync("CT_Snapshot");
        var job = await JobAsync(t, j => j.ReadSnapshot = true);
        await fx.RunExtractAsync(job);
        await fx.ExecAsync(Src, $"INSERT dbo.{t} VALUES (6, N'Fikret', 1, NULL); DELETE dbo.{t} WHERE Id = 2;");
        await fx.RunExtractAsync(job);
        Assert.Equal((1L, 1L, 0L, 1L, "Succeeded"), await LastRunAsync(job));
        Assert.Equal(4, await CountAsync(t));
    }

    [Fact]
    public async Task UntrackedTable_AndWrongKey_FailWithClearMessages()
    {
        var untracked = await SourceAsync("CT_Untracked", track: false);
        var job = await JobAsync(untracked);
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => fx.RunExtractAsync(job));
        Assert.Contains("ENABLE CHANGE_TRACKING", ex.Message);

        var t = await SourceAsync("CT_WrongKey");
        var wrong = await JobAsync(t, j => j.KeyColumns = "Name");
        ex = await Assert.ThrowsAnyAsync<Exception>(() => fx.RunExtractAsync(wrong));
        Assert.Contains("primary key (Id)", ex.Message);
    }
}
