using System.Text.Json;
using SqlDataCollector.Alerts;

namespace SqlDataCollector.Tests.Unit;

public class AlertFormatTests
{
    private static AlertEvent Ev(string type = AlertEvents.Failed, string kind = "Extract", string name = "erp_orders") =>
        new(type, kind, 7, name, 123, DateTime.UtcNow, Error: "Timeout expired\n   at Foo.Bar()");

    private static AlertChannelDef Ch(string statuses = AlertEvents.DefaultChannelStatuses, string kinds = "Extract,Export", string? filter = null, bool active = true) =>
        new() { Name = "c", ChannelType = AlertChannelTypes.Email, Target = "a@b.c", Statuses = statuses, JobKinds = kinds, JobNameFilter = filter, IsActive = active };

    [Fact]
    public void Matches_ByEventKindAndNameFilter()
    {
        Assert.True(AlertFormat.Matches(Ch(), Ev()));
        Assert.False(AlertFormat.Matches(Ch(active: false), Ev()));
        Assert.False(AlertFormat.Matches(Ch(), Ev(AlertEvents.Recovered)));                 // not in the default list
        Assert.True(AlertFormat.Matches(Ch("Failed,Recovered"), Ev(AlertEvents.Recovered)));
        Assert.False(AlertFormat.Matches(Ch(kinds: "Export"), Ev()));
        Assert.True(AlertFormat.Matches(Ch(filter: "crm_*; ERP_*"), Ev()));
        Assert.False(AlertFormat.Matches(Ch(filter: "crm_*"), Ev()));
        // NoServer is not tied to a job: kinds and name filter do not apply
        Assert.True(AlertFormat.Matches(Ch(kinds: "Export", filter: "crm_*"), Ev(AlertEvents.NoServer, "System", "queue sdc_extract")));
    }

    [Fact]
    public void Emails_And_Channels_AreValidated()
    {
        Assert.Empty(AlertFormat.InvalidEmails("a@b.com; c.d@e.org, f@g.net"));
        Assert.Equal(["not-an-email"], AlertFormat.InvalidEmails("a@b.com;not-an-email"));
        Assert.Null(AlertFormat.ValidateChannel(Ch()));
        Assert.Contains("Invalid e-mail", AlertFormat.ValidateChannel(new AlertChannelDef { Name = "x", Target = "nope" }));
        Assert.Contains("absolute http(s)", AlertFormat.ValidateChannel(new AlertChannelDef { Name = "x", ChannelType = AlertChannelTypes.Teams, Target = "ftp://x" }));
        Assert.Contains("Select at least one event", AlertFormat.ValidateChannel(Ch(statuses: "")));
        Assert.Contains("Unknown event", AlertFormat.ValidateChannel(Ch(statuses: "Failed,Exploded")));
    }

    [Fact]
    public void WebhookUrl_IsMasked()
    {
        Assert.Equal("https://prod-01.westeurope.logic.azure.com/…",
            AlertFormat.MaskTarget(AlertChannelTypes.Teams, "https://prod-01.westeurope.logic.azure.com/workflows/abc/triggers/manual/paths/invoke?sig=SECRET"));
        Assert.Equal("a@b.c", AlertFormat.MaskTarget(AlertChannelTypes.Email, "a@b.c"));
    }

    private static readonly DateTime Now = new(2026, 11, 21, 20, 30, 0, DateTimeKind.Utc);
    private static readonly DateTime Long = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static DateTime? Missed(DateTime? lastRun, bool running = false, DateTime? baseline = null, DateTime? modified = null, DateTime? now = null) =>
        AlertFormat.MissedOccurrence("0 20 * * *", "UTC", now ?? Now, TimeSpan.FromMinutes(15), lastRun, running, baseline ?? Long, modified ?? Long);

    [Fact]
    public void MissedOccurrence_DetectsAScheduledRunThatDidNotStart()
    {
        var at20 = new DateTime(2026, 11, 21, 20, 0, 0, DateTimeKind.Utc);
        Assert.Equal(at20, Missed(null));
        Assert.Equal(at20, Missed(at20.AddDays(-1)));                 // yesterday's run only
        Assert.Null(Missed(at20.AddMinutes(1)));                      // ran
        Assert.Null(Missed(at20.AddMinutes(-1)));                     // started a minute early (tolerance)
        Assert.Null(Missed(null, running: true));
        Assert.Null(Missed(null, baseline: at20.AddMinutes(5)));      // alerting started later
        Assert.Null(Missed(null, modified: at20.AddMinutes(5)));      // schedule changed after the occurrence
        // within the grace period today's run is not missed yet; yesterday's (which ran) is the one checked
        Assert.Null(Missed(at20.AddDays(-1), now: at20.AddMinutes(10)));
    }

    [Fact]
    public void Messages_ContainTheEssentials()
    {
        var e = Ev() with { ConsecutiveFailures = 3, HostName = "APP01" };
        Assert.Equal("FAILED: extract job 'erp_orders' (3rd consecutive failure)", AlertFormat.Title(e));
        Assert.Equal("RECOVERED: extract job 'erp_orders' succeeded again", AlertFormat.Title(Ev(AlertEvents.Recovered)));
        var text = AlertFormat.PlainText(e, "https://sdc.corp/");
        Assert.Contains("Error: Timeout expired", text);
        Assert.DoesNotContain("at Foo.Bar", text);                    // first line only
        Assert.Contains("https://sdc.corp/extract/7", text);

        using var hook = JsonDocument.Parse(JsonSerializer.Serialize(AlertFormat.WebhookPayload(e, null)));
        Assert.Equal("Failed", hook.RootElement.GetProperty("event").GetString());
        Assert.Equal("erp_orders", hook.RootElement.GetProperty("jobName").GetString());
        Assert.StartsWith("FAILED:", hook.RootElement.GetProperty("text").GetString());

        using var teams = JsonDocument.Parse(JsonSerializer.Serialize(AlertFormat.TeamsPayload(e, "https://sdc.corp")));
        var att = teams.RootElement.GetProperty("attachments")[0];
        Assert.Equal("application/vnd.microsoft.card.adaptive", att.GetProperty("contentType").GetString());
        Assert.Equal("AdaptiveCard", att.GetProperty("content").GetProperty("type").GetString());
        Assert.Equal("https://sdc.corp/extract/7", att.GetProperty("content").GetProperty("actions")[0].GetProperty("url").GetString());
    }
}
