using SqlDataCollector.Extract;
using SqlDataCollector.Models;
using SqlDataCollector.Services;

namespace SqlDataCollector.Tests.Unit;

public class MergeSqlTests
{
    private static MergePlan Plan(IReadOnlyList<string>? columns = null, bool deleteMissing = false, params string[] xml) =>
        new("stage", "Customer", ["Id", "Region"], columns ?? ["Name", "Doc"], new HashSet<string>(xml, StringComparer.OrdinalIgnoreCase), deleteMissing, 7);

    [Fact]
    public void Merge_UpdatesOnlyChangedRows_ComparingNullSafely_AndInsertsNewKeys()
    {
        var sql = MergeSql.Merge(Plan(xml: "Doc"), delete: false);
        Assert.Contains("UPDATE t SET [Name] = s.[Name], [Doc] = s.[Doc], [_LoadId] = s.[_LoadId]", sql);
        Assert.Contains("ON t.[Id] = s.[Id] AND t.[Region] = s.[Region]", sql);
        Assert.Contains("WHERE EXISTS (SELECT s.[Name], CONVERT(nvarchar(max), s.[Doc])", sql);   // xml is not comparable as is
        Assert.Contains("EXCEPT SELECT t.[Name], CONVERT(nvarchar(max), t.[Doc])", sql);
        Assert.Contains("INSERT [stage].[Customer] ([Id], [Region], [Name], [Doc], [_LoadId], [_LoadedAt], [_ExtractJobId], [_SourceServer])", sql);
        Assert.Contains("WHERE NOT EXISTS (SELECT 1 FROM [stage].[Customer] AS t WHERE t.[Id] = s.[Id] AND t.[Region] = s.[Region])", sql);
        Assert.DoesNotContain("DELETE", sql);
        Assert.Contains("SET XACT_ABORT ON", sql);
    }

    [Fact]
    public void Merge_DeleteOnlyRowsOfThisJob_WhenAsked()
    {
        var sql = MergeSql.Merge(Plan(deleteMissing: true), delete: true);
        Assert.Contains("DELETE t FROM [stage].[Customer] AS t", sql);
        Assert.Contains("t.[_ExtractJobId] = @job AND NOT EXISTS", sql);
    }

    [Fact]
    public void Merge_KeyOnlyTable_HasNoUpdate()
    {
        var sql = MergeSql.Merge(Plan(columns: []), delete: false);
        Assert.DoesNotContain("UPDATE", sql);
        Assert.Contains("INSERT", sql);
    }

    [Fact]
    public void Validation_ChecksNullAndDuplicateKeys()
    {
        var sql = MergeSql.IndexAndValidate(Plan());
        Assert.Contains("CREATE CLUSTERED INDEX CX_sdc_merge ON #sdc_merge ([Id], [Region])", sql);
        Assert.Contains("WHERE [Id] IS NULL OR [Region] IS NULL", sql);
        Assert.Contains("GROUP BY [Id], [Region] HAVING COUNT_BIG(*) > 1", sql);
    }

    [Fact]
    public void Staging_DropsIdentity_ByUnionAll() =>
        Assert.Contains("SELECT TOP (0) * INTO #sdc_merge FROM [stage].[Customer] UNION ALL SELECT TOP (0) * FROM [stage].[Customer]",
            MergeSql.CreateStaging("stage", "Customer"));

    [Theory]
    [InlineData(LoadMode.Append, null, false)]
    [InlineData(LoadMode.Incremental, "ModifiedAt", true)]
    [InlineData(LoadMode.Merge, null, false)]
    [InlineData(LoadMode.Merge, " ", false)]
    [InlineData(LoadMode.Merge, "ModifiedAt", true)]
    public void UsesWatermark(LoadMode mode, string? watermark, bool expected) =>
        Assert.Equal(expected, new ExtractJobDef { LoadMode = mode, WatermarkColumn = watermark }.UsesWatermark());

    [Fact]
    public void KeyColumnList_ParsesBracketsSpacesAndDuplicates() =>
        Assert.Equal(["Id", "Region"], new ExtractJobDef { KeyColumns = " [Id], Region ,id,," }.KeyColumnList());

    [Fact]
    public void Retention_DoesNotApplyToMerge()
    {
        var policy = new RetentionPolicy(90, 365, false, true, "30 1 * * *", 90, 360, 30);
        Assert.Null(policy.EffectiveDays(30, LoadMode.Merge));
        Assert.Null(policy.ValidateJob(0, null, true, false, LoadMode.Merge));
        Assert.Equal(30, policy.EffectiveDays(30, LoadMode.Append));
    }
}
