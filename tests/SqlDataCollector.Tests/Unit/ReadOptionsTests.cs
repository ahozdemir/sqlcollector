using System.Data;
using SqlDataCollector.Extract;
using SqlDataCollector.Models;
using SqlDataCollector.Services;

namespace SqlDataCollector.Tests.Unit;

/// <summary>Parallel reads (range split, slice SQL) and Change Tracking (SQL, version decision, rules).</summary>
public class ReadOptionsTests
{
    // ------------------------------------------------------------------ range split

    [Fact]
    public void Integers_AreSplitIntoEqualSlices_OpenAtBothEnds()
    {
        Assert.Equal(new object[] { 26L, 51L, 76L }, SourceSplitter.Boundaries("int", 1, 100, 4));

        var slices = SourceSplitter.Slices("Id", "INT", 1, 100, 4);
        Assert.Equal(4, slices.Count);
        Assert.Null(slices[0].From);                 // NULLs and anything below MIN
        Assert.Equal(26L, slices[0].To);
        Assert.Equal((26L, 51L), (slices[1].From, slices[1].To));
        Assert.Null(slices[3].To);                   // anything above MAX (rows added after measuring)
        Assert.All(slices, s => Assert.Equal("int", s.SqlType));
    }

    [Theory]
    [InlineData(5, 6, 4, 1)]       // a range of 2 values: 2 slices
    [InlineData(7, 7, 4, 0)]       // one value: one reader is enough
    [InlineData(10, 1, 4, 0)]      // inverted: nothing to split
    [InlineData(1, 1000, 1, 0)]    // one reader asked
    public void SmallRanges_UseFewerReaders(long min, long max, int readers, int boundaries) =>
        Assert.Equal(boundaries, SourceSplitter.Boundaries("bigint", min, max, readers).Count);

    [Fact]
    public void EmptyTable_HasNoSlices() =>
        Assert.Empty(SourceSplitter.Slices("Id", "int", DBNull.Value, null, 8));

    [Fact]
    public void BigintExtremes_DoNotOverflow_AndStayAscending()
    {
        var b = SourceSplitter.Boundaries("bigint", long.MinValue, long.MaxValue, 16).Cast<long>().ToList();
        Assert.Equal(15, b.Count);
        Assert.True(b.Zip(b.Skip(1)).All(p => p.First < p.Second));
        Assert.True(b[0] > long.MinValue && b[^1] < long.MaxValue);
    }

    [Fact]
    public void Dates_AreSplitOnWholeDays()
    {
        var b = SourceSplitter.Boundaries("date", new DateTime(2026, 1, 1), new DateTime(2026, 1, 10), 4).Cast<DateTime>().ToList();
        Assert.Equal(3, b.Count);
        Assert.All(b, d => Assert.Equal(TimeSpan.Zero, d.TimeOfDay));
        Assert.Equal(new DateTime(2026, 1, 3), b[0]);   // 10 days / 4 → +2, +5, +7 days
        Assert.Single(SourceSplitter.Boundaries("date", new DateTime(2026, 1, 1), new DateTime(2026, 1, 2), 8));
    }

    [Fact]
    public void DateTimes_AreSplitOnTicks_ButNotFinerThanTheTypeStores()
    {
        var from = new DateTime(2026, 1, 1, 0, 0, 0);
        Assert.Equal(7, SourceSplitter.Boundaries("datetime2", from, from.AddHours(8), 8).Count);
        Assert.Empty(SourceSplitter.Boundaries("smalldatetime", from, from.AddSeconds(30), 8));    // whole minutes only
        Assert.Single(SourceSplitter.Boundaries("datetime", from, from.AddMilliseconds(20), 8));   // 2 × 10 ms
    }

    [Fact]
    public void UnsupportedSplitType_IsRefused() =>
        Assert.Throws<NotSupportedException>(() => SourceSplitter.Boundaries("uniqueidentifier", Guid.Empty, Guid.Empty, 4));

    [Fact]
    public void SlicePredicates_CoverEverything_WithParametersOfTheColumnType()
    {
        Assert.Equal("([Id] < @SliceTo OR [Id] IS NULL)", new SourceSlice("Id", "int", null, 10L).Predicate());
        Assert.Equal("[Id] >= @SliceFrom AND [Id] < @SliceTo", new SourceSlice("Id", "int", 10L, 20L).Predicate());
        Assert.Equal("[Id] >= @SliceFrom", new SourceSlice("Id", "int", 20L, null).Predicate());

        var p = new SourceSlice("CreatedAt", "datetime", new DateTime(2026, 1, 1), new DateTime(2026, 2, 1)).Parameters().ToList();
        Assert.Equal(new[] { "@SliceFrom", "@SliceTo" }, p.Select(x => x.ParameterName));
        Assert.All(p, x => Assert.Equal(SqlDbType.DateTime, x.SqlDbType));   // the column's real type, not datetime2
        Assert.Equal(SqlDbType.Int, new SourceSlice("Id", "int", 5L, null).Parameters().Single().SqlDbType);
    }

    [Fact]
    public void SliceSql_KeepsFilterAndWatermark()
    {
        var job = new ExtractJobDef
        {
            SourceType = SourceType.Table, SourceSchema = "dbo", SourceTable = "Orders", ColumnList = "[Id], [Amount]",
            FilterClause = "Status <> 'D'", LoadMode = LoadMode.Incremental, WatermarkColumn = "ModifiedAt", UseNoLock = true
        };
        var sql = SourceCommandBuilder.BuildSql(job, hasWatermark: true, new SourceSlice("Id", "int", 10L, 20L));
        Assert.Equal("SELECT [Id], [Amount] FROM [dbo].[Orders] WITH (NOLOCK) WHERE (Status <> 'D') AND [ModifiedAt] > @WatermarkFrom AND [Id] >= @SliceFrom AND [Id] < @SliceTo", sql);
        Assert.StartsWith("SELECT MIN([Id]), MAX([Id]) FROM [dbo].[Orders]", SourceCommandBuilder.BuildSql(job, true, selectList: "MIN([Id]), MAX([Id])"));
    }

    // ------------------------------------------------------------------ change tracking

    private static readonly string[] Pk = ["OrderId", "LineNo"];

    [Fact]
    public void ChangeTracking_FirstRun_ExpiredOrRestoredVersion_ReadsEverything()
    {
        Assert.False(ChangeTrackingSql.Decide(100, 10, null, Pk).IsDelta);
        var expired = ChangeTrackingSql.Decide(100, 50, 40, Pk);
        Assert.False(expired.IsDelta);
        Assert.Contains("older than the change retention", expired.Reason);
        Assert.False(ChangeTrackingSql.Decide(100, 10, 150, Pk).IsDelta);

        var delta = ChangeTrackingSql.Decide(100, 50, 50, Pk);   // exactly the oldest valid version is still readable
        Assert.True(delta.IsDelta);
        Assert.Equal(50, delta.FromVersion);
        Assert.Equal(100, delta.CurrentVersion);
    }

    [Theory]
    [InlineData("ctversion", "123", 123L)]
    [InlineData("datetime", "2026-01-01T00:00:00.0000000", null)]   // a watermark of another mode: start over
    [InlineData("ctversion", "x", null)]
    public void StoredVersion_IsReadOnlyFromAChangeTrackingState(string type, string value, long? expected) =>
        Assert.Equal(expected, ChangeTrackingSql.StoredVersion(new ExtractState { WatermarkType = type, WatermarkValue = value }));

    [Fact]
    public void ChangeTrackingSql_ReadsChangedRowsAndDeletedKeys_WithTheJobsFilter()
    {
        var job = new ExtractJobDef { SourceSchema = "sales", SourceTable = "Lines", ColumnList = "OrderId, LineNo, Qty", FilterClause = "Qty > 0", UseNoLock = true };

        var changed = ChangeTrackingSql.ChangedRows(job, Pk);
        Assert.Equal("SELECT OrderId, LineNo, Qty FROM [sales].[Lines] AS s WITH (NOLOCK) WHERE (Qty > 0) AND EXISTS (SELECT 1 FROM " +
                     "CHANGETABLE(CHANGES [sales].[Lines], @CtFrom) AS ct WHERE ct.[OrderId] = s.[OrderId] AND ct.[LineNo] = s.[LineNo])", changed);

        var deleted = ChangeTrackingSql.DeletedKeys(job, Pk);
        Assert.StartsWith("SELECT ct.[OrderId], ct.[LineNo] FROM CHANGETABLE(CHANGES [sales].[Lines], @CtFrom) AS ct WHERE NOT EXISTS", deleted);
        Assert.EndsWith("WHERE ct.[OrderId] = s.[OrderId] AND ct.[LineNo] = s.[LineNo] AND (Qty > 0))", deleted);   // filtered out = deleted

        job.ReadSnapshot = true;   // a snapshot read needs no NOLOCK
        Assert.DoesNotContain("NOLOCK", ChangeTrackingSql.ChangedRows(job, Pk));
    }

    [Fact]
    public void Merge_DeletesStagedKeysFirst_ThenUpserts()
    {
        var plan = new MergePlan("stage", "Lines", ["OrderId", "LineNo"], ["Qty"], new HashSet<string>(), DeleteMissing: false, 7, DeleteKeys: true);
        var sql = MergeSql.Merge(plan, delete: false);
        var del = sql.IndexOf("DELETE t FROM [stage].[Lines] AS t", StringComparison.Ordinal);
        Assert.True(del >= 0 && del < sql.IndexOf("UPDATE t SET", StringComparison.Ordinal));
        Assert.Contains("t.[_ExtractJobId] = @job AND EXISTS (SELECT 1 FROM #sdc_delete AS d WHERE t.[OrderId] = d.[OrderId] AND t.[LineNo] = d.[LineNo])", sql);
        Assert.Contains("DROP TABLE #sdc_delete;", sql);
        Assert.DoesNotContain("#sdc_delete", MergeSql.Merge(plan with { DeleteKeys = false }, delete: false));
        Assert.Contains("SELECT TOP (0) [OrderId], [LineNo] INTO #sdc_delete FROM [stage].[Lines]", MergeSql.CreateDeleteStaging("stage", "Lines", ["OrderId", "LineNo"]));
    }

    // ------------------------------------------------------------------ rules

    [Fact]
    public void ChangeTracking_IsAMergeOfATable_WithoutWatermark()
    {
        var j = new ExtractJobDef { LoadMode = LoadMode.Merge, UseChangeTracking = true, WatermarkColumn = "ModifiedAt", MergeDeleteMissing = true };
        Assert.True(j.UsesChangeTracking());
        Assert.False(j.UsesWatermark());
        Assert.True(j.UsesStoredState());

        var errs = new List<string>();
        JobDefinitionRules.ValidateReadOptions(j, errs);
        Assert.Empty(errs);
        Assert.Null(j.WatermarkColumn);      // deletes come from change tracking
        Assert.False(j.MergeDeleteMissing);

        var query = new ExtractJobDef { LoadMode = LoadMode.Merge, UseChangeTracking = true, SourceType = SourceType.Query };
        JobDefinitionRules.ValidateReadOptions(query, errs);
        Assert.False(query.UseChangeTracking);
        var append = new ExtractJobDef { LoadMode = LoadMode.Append, UseChangeTracking = true };
        JobDefinitionRules.ValidateReadOptions(append, errs);
        Assert.False(append.UseChangeTracking);
    }

    [Fact]
    public void ParallelReads_OnlyForTables_UpTo16_NotWithASnapshot()
    {
        var j = new ExtractJobDef { ReadParallelism = 4, SplitColumn = " [CreatedAt] " };
        var errs = new List<string>();
        JobDefinitionRules.ValidateReadOptions(j, errs);
        Assert.Empty(errs);
        Assert.Equal("CreatedAt", j.SplitColumn);
        Assert.True(j.UsesParallelRead());

        var query = new ExtractJobDef { SourceType = SourceType.Query, ReadParallelism = 8, SplitColumn = "Id" };
        JobDefinitionRules.ValidateReadOptions(query, errs);
        Assert.Equal(1, query.ReadParallelism);
        Assert.Null(query.SplitColumn);

        JobDefinitionRules.ValidateReadOptions(new ExtractJobDef { ReadParallelism = 17 }, errs);
        Assert.Contains(errs, e => e.Contains("At most 16"));
        errs.Clear();
        JobDefinitionRules.ValidateReadOptions(new ExtractJobDef { ReadParallelism = 2, ReadSnapshot = true }, errs);
        Assert.Contains(errs, e => e.Contains("consistent snapshot"));
    }

    [Fact]
    public void ParallelWrites_UpTo8_NeverForAMerge()
    {
        var errs = new List<string>();
        var append = new ExtractJobDef { LoadMode = LoadMode.Append, WriteParallelism = 4 };
        JobDefinitionRules.ValidateReadOptions(append, errs);
        Assert.Empty(errs);
        Assert.True(append.UsesParallelWrite());

        var merge = new ExtractJobDef { LoadMode = LoadMode.Merge, KeyColumns = "Id", WriteParallelism = 4 };
        Assert.False(merge.UsesParallelWrite());   // also at run time, if a definition was saved without the rules
        JobDefinitionRules.ValidateReadOptions(merge, errs);
        Assert.Equal(1, merge.WriteParallelism);

        var query = new ExtractJobDef { SourceType = SourceType.Query, WriteParallelism = 2 };   // writers do not need a table source
        JobDefinitionRules.ValidateReadOptions(query, errs);
        Assert.Equal(2, query.WriteParallelism);

        JobDefinitionRules.ValidateReadOptions(new ExtractJobDef { WriteParallelism = 9 }, errs);
        Assert.Contains(errs, e => e.Contains("At most 8 parallel writers"));
    }

    [Fact]
    public void RowBufferReader_ReturnsTheBatchesInOrder_AndRethrowsTheProducersError()
    {
        var shape = new RowShape(["Id", "Name"], [typeof(int), typeof(string)], ["int", "nvarchar"]);
        var ok = System.Threading.Channels.Channel.CreateUnbounded<List<object[]>>();
        ok.Writer.TryWrite([[1, "a"], [2, DBNull.Value]]);
        ok.Writer.TryWrite([[3, "c"]]);
        ok.Writer.Complete();
        using (var r = new RowBufferReader(ok.Reader, shape))
        {
            var seen = new List<int>();
            while (r.Read()) { seen.Add(r.GetInt32(0)); if (seen.Count == 2) Assert.True(r.IsDBNull(1)); }
            Assert.Equal(new[] { 1, 2, 3 }, seen);
            Assert.Equal(3, r.RowsRead);
            Assert.Equal("Name", r.GetName(1));
            Assert.Equal(1, r.GetOrdinal("name"));
        }

        var failing = System.Threading.Channels.Channel.CreateUnbounded<List<object[]>>();
        failing.Writer.TryWrite([[1, "a"]]);
        failing.Writer.Complete(new InvalidOperationException("source broke"));
        using var f = new RowBufferReader(failing.Reader, shape);
        Assert.True(f.Read());
        Assert.Equal("source broke", Assert.Throws<InvalidOperationException>(() => f.Read()).Message);   // the original error, not ChannelClosedException
    }

    [Fact]
    public void JobPackage_CarriesTheReadOptions()
    {
        var package = new JobPackage
        {
            ExtractJobs = [new ExtractJobSpec { Name = "x", LoadMode = LoadMode.Merge, UseChangeTracking = true, ReadParallelism = 8, SplitColumn = "Id", WriteParallelism = 4 }]
        };
        var back = Assert.Single(JobPackageJson.Deserialize(JobPackageJson.Serialize(package)).ExtractJobs);
        Assert.True(back.UseChangeTracking);
        Assert.Equal(8, back.ReadParallelism);
        Assert.Equal("Id", back.SplitColumn);
        Assert.Equal(4, back.WriteParallelism);
        // a package written before these options existed reads as one reader and one writer, no change tracking
        var old = Assert.Single(JobPackageJson.Deserialize("""{"format":"SqlDataCollector.Jobs","version":1,"extractJobs":[{"name":"y"}]}""").ExtractJobs);
        Assert.Equal(1, old.ReadParallelism);
        Assert.Equal(1, old.WriteParallelism);
        Assert.False(old.UseChangeTracking);
    }
}
