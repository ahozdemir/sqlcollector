using SqlDataCollector.Models;
using SqlDataCollector.Services;

namespace SqlDataCollector.Tests.Unit;

public class StorageAdvisorTests
{
    [Theory]
    [InlineData(5_000_000, LoadMode.Replace, TargetStorage.Columnstore)]
    [InlineData(800_000, LoadMode.Replace, TargetStorage.Columnstore)]    // 800,000 × 1.25 = 1,000,000: the growth headroom tips it
    [InlineData(790_000, LoadMode.Replace, TargetStorage.Rowstore)]
    [InlineData(8_000_000, LoadMode.Merge, TargetStorage.Columnstore)]    // Merge: 10 M threshold (slower updates), 8 M × 1.25 = 10 M
    [InlineData(7_900_000, LoadMode.Merge, TargetStorage.Rowstore)]
    [InlineData(2_000_000, LoadMode.Merge, TargetStorage.Rowstore)]
    [InlineData(5_000, LoadMode.Replace, TargetStorage.Rowstore)]
    [InlineData(900_000, LoadMode.Incremental, TargetStorage.Columnstore)]
    public void CurrentStateModes_UseTheSourceSize_WithGrowthHeadroom(long rows, LoadMode mode, TargetStorage expected)
    {
        var a = StorageAdvisor.Suggest(rows, mode, retentionDays: 30, runsPerDay: 1);
        Assert.Equal(expected, a.Storage);
        Assert.Equal(rows, a.EstimatedRows);
        Assert.StartsWith(expected.ToString(), a.Reason);
    }

    [Fact]
    public void Merge_ExplainsWhyItStaysRowstore()
    {
        var a = StorageAdvisor.Suggest(2_000_000, LoadMode.Merge, null, 1);
        Assert.Contains("updates are about 2× slower in a columnstore", a.Reason);
        Assert.Contains("10 M", a.Reason);
    }

    [Fact]
    public void Append_MultipliesTheSourceByTheLoadsThatAreKept()
    {
        // 50,000 rows a night kept for 30 days = 1.5 M rows in the target, although the source is small
        var daily = StorageAdvisor.Suggest(50_000, LoadMode.Append, retentionDays: 30, runsPerDay: 1);
        Assert.Equal(TargetStorage.Columnstore, daily.Storage);
        Assert.Equal(1_500_000, daily.EstimatedRows);
        Assert.Contains("50,000 source rows × 1 load(s) a day × 30 days kept", daily.Reason);

        // the same table kept for 7 days stays small
        Assert.Equal(TargetStorage.Rowstore, StorageAdvisor.Suggest(50_000, LoadMode.Append, 7, 1).Storage);
        // keep forever: one year is planned
        var forever = StorageAdvisor.Suggest(5_000, LoadMode.Append, null, 1);
        Assert.Equal(1_825_000, forever.EstimatedRows);
        Assert.Contains("kept forever", forever.Reason);
        // every 15 minutes
        Assert.Equal(TargetStorage.Columnstore, StorageAdvisor.Suggest(1_000, LoadMode.Append, 14, 96).Storage);
    }

    [Fact]
    public void UnknownSizes_AndUnsupportedColumns_SuggestRowstore()
    {
        var unknown = StorageAdvisor.Suggest(null, LoadMode.Replace, 30, 1);
        Assert.Equal(TargetStorage.Rowstore, unknown.Storage);
        Assert.Null(unknown.EstimatedRows);
        Assert.Contains("unknown", unknown.Reason);

        var xml = StorageAdvisor.Suggest(50_000_000, LoadMode.Replace, 30, 1, ["Doc"]);
        Assert.Equal(TargetStorage.Rowstore, xml.Storage);
        Assert.Contains("Doc", xml.Reason);

        Assert.Equal(["Doc", "Any"], StorageAdvisor.UnsupportedColumns([("Id", "int"), ("Doc", "xml"), ("Any", "sql_variant"), ("Name", "nvarchar(50)")]));
    }

    [Theory]
    [InlineData("0 2 * * *", 1)]
    [InlineData("*/15 * * * *", 96)]
    [InlineData("0 */6 * * *", 4)]
    public void RunsPerDay_CountsTheScheduleOverAWeek(string cron, double expected) =>
        Assert.Equal(expected, StorageAdvisor.RunsPerDay(JobTriggerMode.Schedule, cron, "UTC"), 1);

    [Fact]
    public void RunsPerDay_WeeklyAndTriggeredJobs()
    {
        Assert.Equal(1 / 7.0, StorageAdvisor.RunsPerDay(JobTriggerMode.Schedule, "0 3 * * 1", "UTC"), 3);
        Assert.Equal(1, StorageAdvisor.RunsPerDay(JobTriggerMode.AfterJobs, "0 3 * * 1", "UTC"));
        Assert.Equal(1, StorageAdvisor.RunsPerDay(JobTriggerMode.Schedule, "not a cron", "UTC"));
        Assert.Equal("13.5 M", StorageAdvisor.Format(13_500_000));
        Assert.Equal("950,000", StorageAdvisor.Format(950_000));
    }
}
