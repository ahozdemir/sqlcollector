using SqlDataCollector.Models;
using SqlDataCollector.Quality;

namespace SqlDataCollector.Tests.Unit;

public class QualityRulesTests
{
    [Theory]
    [InlineData(0, "=", 0, true)]
    [InlineData(1, "=", 0, false)]
    [InlineData(1, "<>", 0, true)]
    [InlineData(5, "<", 10, true)]
    [InlineData(10, "<=", 10, true)]
    [InlineData(11, ">", 10, true)]
    [InlineData(9, ">=", 10, false)]
    public void Compare(decimal value, string op, decimal threshold, bool expected) =>
        Assert.Equal(expected, QualityRules.Compare(value, op, threshold));

    [Fact]
    public void Compare_UnknownOperator_Throws() => Assert.Throws<InvalidOperationException>(() => QualityRules.Compare(1, "~", 1));

    [Fact]
    public void Worst_FailedBeatsWarningBeatsPassed()
    {
        Assert.Null(QualityRules.Worst([]));
        Assert.Equal("Passed", QualityRules.Worst(["Passed", "Passed"]));
        Assert.Equal("Warning", QualityRules.Worst(["Passed", "Warning"]));
        Assert.Equal("Failed", QualityRules.Worst(["Warning", "Failed", "Passed"]));
    }

    [Fact]
    public void Deviation_NeedsHistory_AndAvoidsDivisionByZero()
    {
        Assert.Null(QualityRules.Deviation(100, [100, 100]));            // too little history
        Assert.Null(QualityRules.Deviation(100, [0, 0, 0]));             // average 0
        Assert.Equal((20m, 100m), QualityRules.Deviation(80, [100, 90, 110]));
        Assert.Equal((150m, 100m), QualityRules.Deviation(250, [100, 100, 100]));
    }

    [Fact]
    public void Sql_UsesQuotedIdentifiers_AndTheScope()
    {
        var nulls = QualityRules.NullRateSql("erp", "Customers", "Tax No", "Load");
        Assert.Contains("CASE WHEN [Tax No] IS NULL", nulls);
        Assert.Contains("FROM [erp].[Customers] WHERE [_LoadId] = @load", nulls);

        var dup = QualityRules.DuplicatesSql("erp", "Customers", ["CustomerId", "Region"], "Table");
        Assert.Contains("SELECT [CustomerId], [Region] FROM [erp].[Customers] WHERE [_ExtractJobId] = @job", dup);
        Assert.Contains("GROUP BY [CustomerId], [Region] HAVING COUNT_BIG(*) > 1", dup);

        var job = new ExtractJobDef { SourceSchema = "dbo", SourceTable = "Orders", FilterClause = "Status <> 'D'", UseNoLock = true };
        Assert.Equal("SELECT COUNT_BIG(*) FROM [dbo].[Orders] WITH (NOLOCK) WHERE (Status <> 'D')", QualityRules.SourceCountSql(job));
    }

    [Fact]
    public void Validate_RefusesChecksThatCannotWork()
    {
        var table = new ExtractJobDef { SourceType = SourceType.Table, LoadMode = LoadMode.Replace };
        var query = new ExtractJobDef { SourceType = SourceType.Query, LoadMode = LoadMode.Append };
        var incremental = new ExtractJobDef { SourceType = SourceType.Table, LoadMode = LoadMode.Incremental };

        Assert.Null(QualityRules.Validate(new QualityCheckDef { CheckType = QualityCheckTypes.SourceCount }, table));
        Assert.NotNull(QualityRules.Validate(new QualityCheckDef { CheckType = QualityCheckTypes.SourceCount }, query));
        Assert.NotNull(QualityRules.Validate(new QualityCheckDef { CheckType = QualityCheckTypes.SourceCount }, incremental));
        Assert.NotNull(QualityRules.Validate(new QualityCheckDef { CheckType = QualityCheckTypes.NullRate }, table));
        Assert.NotNull(QualityRules.Validate(new QualityCheckDef { CheckType = QualityCheckTypes.Duplicates, ColumnName = " , " }, table));
        Assert.NotNull(QualityRules.Validate(new QualityCheckDef { CheckType = QualityCheckTypes.CustomSql, SqlText = "SELECT 1", Operator = "=" }, table));
        Assert.Null(QualityRules.Validate(new QualityCheckDef { CheckType = QualityCheckTypes.CustomSql, SqlText = "SELECT 1", Operator = "=", Threshold = 1 }, table));
        Assert.NotNull(QualityRules.Validate(new QualityCheckDef { CheckType = QualityCheckTypes.MinRows, Threshold = -1 }, table));
    }

    [Fact]
    public void Columns_ParsesBracketsAndSpaces() => Assert.Equal(["Id", "Region"], QualityRules.Columns(" [Id], Region ,,"));
}
