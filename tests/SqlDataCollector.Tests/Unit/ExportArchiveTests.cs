using SqlDataCollector.Export;
using SqlDataCollector.Options;

namespace SqlDataCollector.Tests.Unit;

/// <summary>The export Worker's local archive: 1–3 days, a full export of the same day replaces its copy, incremental files add up.</summary>
public sealed class ExportArchiveTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sdc-archive-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch (DirectoryNotFoundException) { } }

    private string File(string relative, string content = "x")
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllText(path, content);
        return path;
    }

    private string[] Days() => Directory.GetDirectories(_root).Select(Path.GetFileName).Order().ToArray()!;

    [Theory]
    [InlineData(-5, 1)]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 3)]
    [InlineData(7, 3)]   // older configurations (default was 7) are capped
    public void Days_AreAlwaysOneToThree(int configured, int expected) =>
        Assert.Equal(expected, ExportArchive.Days(new Dictionary<string, string?>(), new CollectorOptions { LocalFileRetentionDays = configured }));

    [Fact]
    public void Days_TheSettingsPageValueWinsOverTheConfiguration()
    {
        var o = new CollectorOptions { LocalFileRetentionDays = 1 };
        Assert.Equal(3, ExportArchive.Days(new Dictionary<string, string?> { [ExportArchive.DaysKey] = "3" }, o));
        Assert.Equal(1, ExportArchive.Days(new Dictionary<string, string?> { [ExportArchive.DaysKey] = "abc" }, o));
        Assert.Equal(1, new CollectorOptions().LocalFileRetentionDays);
        Assert.True(new CollectorOptions().KeepLocalFiles);   // on by default
    }

    [Fact]
    public void Store_FullExport_ReplacesTheSameDaysCopy()
    {
        var target = Path.Combine(_root, "2026-11-26", "orders_full");
        ExportArchive.Store([File("run1/Orders_part0001.csv.gz", "first"), File("run1/Orders_part0002.csv.gz", "first")], target, replace: true, "run1");
        ExportArchive.Store([File("run2/Orders_part0001.csv.gz", "second")], target, replace: true, "run2");

        var files = Directory.GetFiles(target);
        Assert.Equal("Orders_part0001.csv.gz", Path.GetFileName(Assert.Single(files)));   // part 2 of the first run is gone too
        Assert.Equal("second", System.IO.File.ReadAllText(files[0]));
        Assert.False(System.IO.File.Exists(Path.Combine(_root, "run2", "Orders_part0001.csv.gz")));   // moved, not copied
    }

    [Fact]
    public void Store_IncrementalExport_AddsItsFilesNextToTheEarlierOnes()
    {
        var target = Path.Combine(_root, "2026-11-26", "dwh_nightly", "erp.Orders");
        ExportArchive.Store([File("w1/Orders_part0001.csv.gz", "loads 1-5")], target, replace: false, "run41");
        ExportArchive.Store([File("w2/Orders_part0001.csv.gz", "loads 6-7")], target, replace: false, "run42");

        Assert.Equal(["run41_Orders_part0001.csv.gz", "run42_Orders_part0001.csv.gz"], Directory.GetFiles(target).Select(Path.GetFileName).Order());
    }

    [Fact]
    public void Cleanup_KeepsTheNewestDaysThatHoldFiles()
    {
        foreach (var d in new[] { "2026-11-21", "2026-11-23", "2026-11-24", "2026-11-25", "2026-11-26" }) File($"{d}/job/a.csv.gz");
        Directory.CreateDirectory(Path.Combine(_root, "not-a-day"));

        var deleted = ExportArchive.Cleanup(_root, 2, new DateTime(2026, 11, 26));

        Assert.Equal(3, deleted.Count);
        Assert.Equal(["2026-11-25", "2026-11-26", "not-a-day"], Days());
    }

    [Fact]
    public void Cleanup_KeepsYesterdayUntilTodayHasFiles()
    {
        File("2026-11-25/job/a.csv.gz");
        Directory.CreateDirectory(Path.Combine(_root, "2026-11-26", "job_77"));   // today's run is still writing / failed

        ExportArchive.Cleanup(_root, 1, new DateTime(2026, 11, 26));
        Assert.Equal(["2026-11-25", "2026-11-26"], Days());   // one day of files always stays

        File("2026-11-26/job/b.csv.gz");
        ExportArchive.Cleanup(_root, 1, new DateTime(2026, 11, 26));
        Assert.Equal(["2026-11-26"], Days());
    }

    [Fact]
    public void Cleanup_WithoutArchivedFiles_RemovesOnlyEarlierLeftovers()
    {
        Directory.CreateDirectory(Path.Combine(_root, "2026-11-20", "job_1"));
        Directory.CreateDirectory(Path.Combine(_root, "2026-11-26", "job_2"));
        ExportArchive.Cleanup(_root, 3, new DateTime(2026, 11, 26));
        Assert.Equal(["2026-11-26"], Days());
    }

    [Fact]
    public void Cleanup_OfAMissingFolder_DoesNothing() =>
        Assert.Empty(ExportArchive.Cleanup(Path.Combine(_root, "missing"), 1, DateTime.Today));
}
