using SqlDataCollector.Infrastructure;
using SqlDataCollector.Models;
using SqlDataCollector.Services;

namespace SqlDataCollector.Tests.Unit;

public class PermissionRulesTests
{
    private static CheckContext Ctx(string role = CheckContext.Worker, string? queue = CollectorQueues.Extract, bool webOnly = false,
        bool autoCreate = false, bool prepare = false, bool shared = false) =>
        new(role, queue, webOnly, autoCreate, prepare, shared, "hangfire_collector");

    private static bool Has(IEnumerable<ProbeGroup> groups, string permission, string? securable = null) =>
        groups.SelectMany(g => g.Probes).Any(p => p.Permission == permission && (securable is null || p.Securable == securable));

    [Fact]
    public void Metadata_RequiresDdl_OnlyWithAutoCreateSchema()
    {
        var plain = PermissionRules.Metadata(Ctx());
        Assert.Single(plain);
        Assert.All(plain[0].Probes, p => Assert.Equal("etl", p.Securable));
        Assert.False(Has(plain, "CREATE TABLE"));

        var auto = PermissionRules.Metadata(Ctx(autoCreate: true));
        Assert.Equal(2, auto.Count);
        Assert.True(Has(auto, "CREATE TABLE"));
        Assert.True(Has(auto, "ALTER", "etl"));
    }

    [Fact]
    public void Hangfire_Shared_ForbidsRightsOutsideOurSchema_EmbeddedDoesNot()
    {
        Assert.DoesNotContain(PermissionRules.Hangfire(Ctx()), g => g.Kind == ProbeKind.Forbidden);

        var shared = PermissionRules.Hangfire(Ctx(shared: true));
        var forbidden = Assert.Single(shared, g => g.Kind == ProbeKind.Forbidden);
        Assert.Contains(forbidden.Probes, p => p.Permission == "CONTROL" && p.SecurableClass == "DATABASE");
        Assert.All(shared.Single(g => g.Item.StartsWith("Read/write")).Probes, p => Assert.Equal("hangfire_collector", p.Securable));
    }

    [Fact]
    public void Target_ExtractWorker_NeedsDmlDdlAndDataspaceForPartitions()
    {
        var groups = PermissionRules.Target(Ctx(), [new TargetSchema("stage", true)], partitionDaily: true);
        Assert.True(Has(groups, "INSERT", "stage"));
        Assert.True(Has(groups, "DELETE", "stage"));
        Assert.True(Has(groups, "CREATE TABLE"));
        Assert.True(Has(groups, "ALTER", "stage"));
        Assert.True(Has(groups, "ALTER ANY DATASPACE"));
        Assert.False(Has(groups, "CREATE SCHEMA"));
        Assert.DoesNotContain(groups, g => g.Kind == ProbeKind.Forbidden);

        Assert.False(Has(PermissionRules.Target(Ctx(), [new TargetSchema("stage", true)], partitionDaily: false), "ALTER ANY DATASPACE"));
    }

    [Fact]
    public void Target_MissingSchema_FallsBackToDatabaseLevel_AndNeedsCreateSchema()
    {
        var groups = PermissionRules.Target(Ctx(queue: CollectorQueues.System), [new TargetSchema("newschema", false)], false);
        var load = groups.Single(g => g.Item == "Load and clean up data");
        Assert.All(load.Probes, p => Assert.Equal("DATABASE", p.SecurableClass));
        Assert.True(Has(groups, "CREATE SCHEMA"));
    }

    [Fact]
    public void Target_WebOnly_ForbidsWrites_EmbeddedWebDoesNot()
    {
        var webOnly = PermissionRules.Target(Ctx(CheckContext.Web, null, webOnly: true), [new TargetSchema("stage", true)], true);
        Assert.Contains(webOnly, g => g.Kind == ProbeKind.Forbidden && g.Probes.Any(p => p.Permission == "INSERT"));
        Assert.True(Has(webOnly, "VIEW DEFINITION"));
        Assert.False(Has(webOnly.Where(g => g.Kind == ProbeKind.Required), "INSERT"));

        var embedded = PermissionRules.Target(Ctx(CheckContext.Web, null, webOnly: false), [new TargetSchema("stage", true)], true);
        Assert.DoesNotContain(embedded, g => g.Kind == ProbeKind.Forbidden);
    }

    [Fact]
    public void Target_ExportWorker_OnlyReads()
    {
        var groups = PermissionRules.Target(Ctx(queue: CollectorQueues.Export), [new TargetSchema("stage", true)], true);
        var g = Assert.Single(groups);
        Assert.All(g.Probes, p => Assert.Equal("SELECT", p.Permission));
    }

    [Fact]
    public void Source_ChecksObjects_ForbidsWrites_AndIsSkippedForExportAndSystem()
    {
        SourceObject[] objects =
        [
            new(SourceObjectKind.Table, "[dbo].[Orders]"),
            new(SourceObjectKind.Procedure, "dbo.usp_DailySales"),
            new(SourceObjectKind.Query, null)
        ];
        var groups = PermissionRules.Source(Ctx(), objects);
        Assert.Contains(groups, g => g.Kind == ProbeKind.Required && g.Probes.Contains(PermissionProbe.Object("SELECT", "[dbo].[Orders]")));
        Assert.Contains(groups, g => g.Kind == ProbeKind.Required && g.Probes.Contains(PermissionProbe.Object("EXECUTE", "dbo.usp_DailySales")));
        Assert.Contains(groups, g => g.Kind == ProbeKind.Advisory);
        var forbidden = Assert.Single(groups, g => g.Kind == ProbeKind.Forbidden);
        Assert.Contains(PermissionProbe.Database("DELETE"), forbidden.Probes);
        Assert.Contains(PermissionProbe.Schema("UPDATE", "dbo"), forbidden.Probes);

        Assert.Empty(PermissionRules.Source(Ctx(queue: CollectorQueues.Export), objects));
        Assert.Empty(PermissionRules.Source(Ctx(queue: CollectorQueues.System), objects));
        Assert.NotEmpty(PermissionRules.Source(Ctx(CheckContext.Web, null, webOnly: true), objects));
    }

    [Fact]
    public void ExportReader_MustRead_AndMustNotWrite()
    {
        var groups = PermissionRules.ExportReader("sdc_export_reader", [new TargetSchema("stage", true)]);
        var read = Assert.Single(groups, g => g.Kind == ProbeKind.Required);
        Assert.Contains(PermissionProbe.Schema("SELECT", "stage"), read.Probes);
        var forbidden = Assert.Single(groups, g => g.Kind == ProbeKind.Forbidden);
        Assert.Contains(PermissionProbe.Database("CREATE TABLE"), forbidden.Probes);
        Assert.Contains(PermissionProbe.Schema("DELETE", "stage"), forbidden.Probes);
        Assert.All(groups, g => Assert.Contains("sdc_export_reader", g.Item));
    }

    [Fact]
    public void Evaluate_Required_ListsMissing_AndMarksInvisibleObjects()
    {
        var g = new ProbeGroup("x", ProbeKind.Required, [PermissionProbe.Database("SELECT"), PermissionProbe.Object("SELECT", "[dbo].[Gone]")]);
        Assert.Equal(CheckStatus.Ok, PermissionRules.Evaluate(g, [true, true]).Status);

        var (status, detail) = PermissionRules.Evaluate(g, [true, null]);
        Assert.Equal(CheckStatus.Missing, status);
        Assert.Contains("SELECT on [dbo].[Gone] (not found or not visible)", detail);
    }

    [Fact]
    public void Evaluate_Forbidden_ReportsExcess_WithSysadminNote()
    {
        var g = new ProbeGroup("x", ProbeKind.Forbidden, [PermissionProbe.Database("INSERT"), PermissionProbe.Database("CONTROL")]);
        Assert.Equal(CheckStatus.Ok, PermissionRules.Evaluate(g, [false, null]).Status);

        var (status, detail) = PermissionRules.Evaluate(g, [true, false], isSysadmin: true);
        Assert.Equal(CheckStatus.Excess, status);
        Assert.Contains("Granted but not needed: INSERT.", detail);
        Assert.Contains("sysadmin", detail);
    }

    [Fact]
    public void Evaluate_Advisory_IsOnlyAWarning() =>
        Assert.Equal(CheckStatus.Warning,
            PermissionRules.Evaluate(new ProbeGroup("x", ProbeKind.Advisory, [PermissionProbe.Database("SELECT")]), [false]).Status);

    [Theory]
    [InlineData("EXEC dbo.usp_DailySales @Days = 30", SourceObjectKind.Procedure, "dbo.usp_DailySales")]
    [InlineData("  execute [crm].[usp Report] 1", SourceObjectKind.Procedure, "[crm].[usp Report]")]
    [InlineData("EXEC @rc = usp_X", SourceObjectKind.Procedure, "usp_X")]
    [InlineData("SELECT * FROM dbo.Orders WHERE ModifiedAt > @WatermarkFrom", SourceObjectKind.Query, null)]
    public void SourceObjectOf_DetectsProcedures(string sql, SourceObjectKind kind, string? name)
    {
        var o = PermissionRules.SourceObjectOf(new ExtractJobDef { SourceType = SourceType.Query, SourceQuery = sql });
        Assert.Equal(kind, o.Kind);
        Assert.Equal(name, o.Name);
    }

    [Fact]
    public void SourceObjectOf_Table_IsQuotedWithDefaultSchema() =>
        Assert.Equal("[dbo].[Orders]", PermissionRules.SourceObjectOf(new ExtractJobDef { SourceType = SourceType.Table, SourceTable = "Orders" }).Name);
}
