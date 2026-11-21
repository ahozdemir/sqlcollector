using SqlDataCollector.Services;

namespace SqlDataCollector.Tests.Unit;

public class RetentionPolicyTests
{
    private static RetentionPolicy P(int? def = null, int? max = null, bool forever = true, bool editors = true, string cron = "30 1 * * *") =>
        new(def, max, forever, editors, cron, 90, 360, 30);

    [Theory]
    [InlineData(null, null, true, null, null)]      // no policy: job value as is
    [InlineData(null, null, true, 400, 400)]
    [InlineData(null, 365, true, 400, 365)]         // capped
    [InlineData(null, 365, true, 30, 30)]
    [InlineData(null, 365, true, null, null)]       // keep forever allowed
    [InlineData(90, 365, false, null, 90)]          // keep forever not allowed -> default
    [InlineData(null, 365, false, null, 365)]       // ... or the maximum
    public void EffectiveDays(int? def, int? max, bool forever, int? job, int? expected) =>
        Assert.Equal(expected, P(def, max, forever).EffectiveDays(job));

    [Fact]
    public void Replace_HasNoRetention_EvenWhenKeepForeverIsNotAllowed()
    {
        var p = P(def: 90, max: 365, forever: false, editors: false);
        Assert.Null(p.EffectiveDays(null, Models.LoadMode.Replace));
        Assert.Null(p.EffectiveDays(30, Models.LoadMode.Replace));
        Assert.Null(p.ValidateJob(null, null, isNew: true, isAdmin: false, Models.LoadMode.Replace));
        Assert.Equal(90, p.EffectiveDays(null, Models.LoadMode.Append));
    }

    [Fact]
    public void ValidateJob_EnforcesMaximumAndKeepForever()
    {
        var p = P(def: 90, max: 365, forever: false);
        Assert.Null(p.ValidateJob(100, null, isNew: true, isAdmin: false));
        Assert.Contains("cannot exceed 365", p.ValidateJob(400, null, true, false));
        Assert.Contains("Keep forever is not allowed", p.ValidateJob(null, null, true, false));
        Assert.Contains("cannot exceed 365", p.ValidateJob(400, null, true, isAdmin: true));   // Admins follow the policy too
        Assert.Contains("at least 1 day", p.ValidateJob(0, null, true, true));
    }

    [Fact]
    public void ValidateJob_EditorsMayNotChange_OnlyDefaultOrUnchangedValue()
    {
        var p = P(def: 90, max: 365, forever: false, editors: false);
        Assert.Null(p.ValidateJob(90, null, isNew: true, isAdmin: false));
        Assert.Contains("Only Admins", p.ValidateJob(30, null, isNew: true, isAdmin: false));
        Assert.Contains("Only Admins", p.ValidateJob(30, 90, isNew: false, isAdmin: false));
        // an old job saved before the policy (keep forever) can still be saved unchanged; the retention job caps it
        Assert.Null(p.ValidateJob(null, null, isNew: false, isAdmin: false));
        Assert.Null(p.ValidateJob(30, 90, isNew: false, isAdmin: true));
    }

    [Fact]
    public void Validate_Policy()
    {
        Assert.Empty(P().Validate());
        Assert.Empty(P(def: 90, max: 365, forever: false).Validate());
        Assert.Contains(P(def: 400, max: 365).Validate(), e => e.Contains("longer than the maximum"));
        Assert.Contains(P(forever: false).Validate(), e => e.Contains("set a default or a maximum"));
        Assert.Contains(P(cron: "61 * * * *").Validate(), e => e.Contains("Invalid retention schedule"));
        Assert.Contains(new RetentionPolicy(null, null, true, true, "0 1 * * *", 0, 1, 1).Validate(), e => e.Contains("Run history"));
    }
}
