using SqlDataCollector.Components.Shared;
using SqlDataCollector.Models;

namespace SqlDataCollector.Tests.Unit;

public class ChartTests
{
    private static readonly DateTime From = new(2026, 11, 22, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime To = From.AddHours(8);

    [Fact]
    public void Bar_PositionsRunsInTheWindow_AndKeepsShortRunsVisible()
    {
        Assert.Equal((25.0, 12.5), ChartMath.Bar(From.AddHours(2), From.AddHours(3), From, To, To));
        // a 3 second run still gets the minimum width
        Assert.Equal(0.4, ChartMath.Bar(From.AddHours(4), From.AddHours(4).AddSeconds(3), From, To, To)!.Value.Width);
        // running: ends now
        Assert.Equal((50.0, 25.0), ChartMath.Bar(From.AddHours(4), null, From, To, From.AddHours(6)));
        // clipped at the edges, outside = null
        Assert.Equal((0.0, 12.5), ChartMath.Bar(From.AddHours(-3), From.AddHours(1), From, To, To));
        Assert.Null(ChartMath.Bar(From.AddHours(-3), From.AddHours(-1), From, To, To));
        Assert.Null(ChartMath.Bar(To.AddHours(1), To.AddHours(2), From, To, To));
        // a minimum width bar at the very end stays inside
        var end = ChartMath.Bar(To.AddSeconds(-1), To, From, To, To)!.Value;
        Assert.True(end.Left + end.Width <= 100.0001);
    }

    [Fact]
    public void Ticks_FollowTheDisplayTimeZone_AndTheWindowLength()
    {
        var istanbul = TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul");   // UTC+3
        var eight = ChartMath.Ticks(From, To, istanbul);
        Assert.Equal(7, eight.Count);                      // 03:00–11:00 local: hourly ticks 04:00 … 10:00
        Assert.Equal("04:00", eight[0].Label);
        Assert.Equal(12.5, eight[0].Pct);
        var day = ChartMath.Ticks(From, From.AddHours(24), TimeZoneInfo.Utc);
        Assert.Equal(11, day.Count);                       // every 2 hours: 02 … 22
        Assert.All(day, t => Assert.Equal(0, int.Parse(t.Label[..2]) % 2));
        var three = ChartMath.Ticks(From, From.AddHours(72), TimeZoneInfo.Utc);
        Assert.Contains(three, t => t.Label == "23 Nov");   // midnight shows the date on long windows
    }

    [Theory]
    [InlineData("Succeeded", null, "ok")]
    [InlineData("Succeeded", "Warning", "warn")]
    [InlineData("Succeeded", "Failed", "err")]
    [InlineData("PartiallyFailed", null, "warn")]
    [InlineData("Failed", null, "err")]
    [InlineData("Abandoned", null, "err")]
    [InlineData("Running", null, "run")]
    [InlineData("Skipped", null, "skip")]
    public void Tone_ColoursOutcomes(string status, string? quality, string tone) =>
        Assert.Equal(tone, ChartMath.Tone(new RunPoint { Status = status, QualityStatus = quality }));

    [Fact]
    public void Sparkline_ScalesIntoTheBox_SkipsGaps_AndCentresFlatSeries()
    {
        var p = ChartMath.Sparkline([0, 50, 100], 104, 22);
        Assert.Equal([(2.0, 20.0), (52.0, 11.0), (102.0, 2.0)], p);
        var gap = ChartMath.Sparkline([10, null, 30], 104, 22);
        Assert.Equal(2, gap.Count);
        Assert.Equal(102.0, gap[1].X);                      // the gap keeps its place
        Assert.All(ChartMath.Sparkline([5, 5, 5], 104, 22), q => Assert.Equal(11.0, q.Y));
        Assert.Empty(ChartMath.Sparkline([null, null], 104, 22));
        Assert.Equal("2,20 52,11", ChartMath.Points(p.Take(2)));
    }

    [Fact]
    public void LastIsOutlier_FlagsSharpChangesAgainstTheMedian()
    {
        double?[] steady = [5000, 5010, 4990, 5005, 5000, 5002];
        Assert.False(ChartMath.LastIsOutlier(steady));
        Assert.True(ChartMath.LastIsOutlier([5000, 5010, 4990, 5005, 5000, 1212]));     // dropped to a quarter
        Assert.True(ChartMath.LastIsOutlier([100, 110, 90, 105, 100, 260]));            // more than doubled
        Assert.False(ChartMath.LastIsOutlier([5000, 5010, 1212]));                      // too few runs to tell
        Assert.False(ChartMath.LastIsOutlier([0, 0, 0, 0, 0, 0]));                      // incremental job with nothing new
        Assert.True(ChartMath.LastIsOutlier([0, 0, 0, 0, 0, 75]));
        Assert.False(ChartMath.LastIsOutlier([0, 100, 0, 75, 0, 50]));                  // usually busy, sometimes empty
    }

    private static PipeJob Job(string kind, int id, string name, string? status = "Succeeded", bool after = false, string? quality = null,
        string? destination = null, bool active = true) =>
        new(new JobRef(kind, id), name, active, after, status, quality, From, 1000, destination, destination is null ? null : StorageProviders.S3);

    [Fact]
    public void Pipeline_PutsJobsInColumnsByChainLength_AndDestinationsLast()
    {
        var a = Job(JobRef.Extract, 1, "crm_contacts");
        var b = Job(JobRef.Extract, 2, "erp_customers", quality: "Warning");
        var c = Job(JobRef.Extract, 3, "erp_merge", status: "Failed");
        var lone = Job(JobRef.Extract, 4, "not_shown");
        var all = Job(JobRef.Export, 10, "dwh_all", after: true, destination: "s3-prod");
        var feed = Job(JobRef.Export, 11, "erp_feed", after: true, destination: "s3-prod");
        var g = PipelineLayout.Build([a, b, c, lone, all, feed],
            [(all.Ref, a.Ref), (all.Ref, b.Ref), (feed.Ref, c.Ref)], t => "00:00");

        Assert.Equal(1, g.HiddenJobs);                                     // an extract without relations is not drawn
        var x = g.Nodes.ToDictionary(n => n.Id);
        Assert.Equal(x["x:1"].X, x["x:3"].X);                             // upstreams in the first column
        Assert.True(x["e:10"].X > x["x:1"].X);
        Assert.True(x["d:s3-prod"].X > x["e:10"].X);                      // destination last
        Assert.Equal(["Starts on its schedule", "Runs after the jobs on the left", "Destinations"], g.Columns.Select(col => col.Title));

        Assert.Equal("warn", x["x:2"].Tone);
        Assert.Equal("err", x["x:3"].Tone);
        Assert.Equal("ok", x["e:10"].Tone);                                // its upstreams succeeded (a quality warning does not block)
        Assert.Equal("wait", x["e:11"].Tone);                              // waits for the failed merge
        Assert.Equal("Waiting for erp_merge", x["e:11"].Sub);
        Assert.Contains(g.Edges, e => e.From == "x:3" && e.To == "e:11" && e.Dashed && e.Tone == "err");
        Assert.Equal(2, g.Edges.Count(e => e.To == "d:s3-prod"));
        Assert.Equal("Amazon S3 / compatible", x["d:s3-prod"].Sub);
    }

    [Fact]
    public void Pipeline_ChainsGetDeeperColumns_RowsFollowUpstreams_LabelsAreShortened()
    {
        var a = Job(JobRef.Extract, 1, "a_first");
        var b = Job(JobRef.Extract, 2, "b_second", after: true);
        var z = Job(JobRef.Extract, 3, "z_other");
        var y = Job(JobRef.Extract, 4, "y_after_other", after: true);
        var e = Job(JobRef.Export, 10, "an_export_with_a_really_long_name_indeed", after: true, destination: "gcs");
        var off = Job(JobRef.Export, 11, "inactive_export", active: false, destination: "gcs");
        var g = PipelineLayout.Build([a, b, z, y, e, off], [(b.Ref, a.Ref), (e.Ref, b.Ref), (y.Ref, z.Ref)], t => "t");
        var n = g.Nodes.ToDictionary(q => q.Id);

        Assert.True(n["x:2"].X > n["x:1"].X && n["e:10"].X > n["x:2"].X);   // a → b → export: three columns
        Assert.True(n["x:1"].Y < n["x:3"].Y);                               // first column by name
        Assert.True(n["x:2"].Y < n["x:4"].Y);                               // second column follows the rows of the upstreams
        Assert.EndsWith("…", n["e:10"].Label);
        Assert.Equal(28, n["e:10"].Label.Length);
        Assert.Equal("an_export_with_a_really_long_name_indeed", n["e:10"].FullName);
        Assert.Equal("muted", n["e:11"].Tone);
        Assert.Equal("inactive", n["e:11"].Sub);
        Assert.Empty(PipelineLayout.Build([a], [], t => "").Nodes);
    }
}
