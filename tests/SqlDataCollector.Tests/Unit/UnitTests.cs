using System.Data;
using System.Globalization;
using SqlDataCollector.Export;
using SqlDataCollector.Extract;
using SqlDataCollector.Security;
using SqlDataCollector.Services;

namespace SqlDataCollector.Tests.Unit;

public class ObjectNameTemplateTests
{
    private static readonly DateTime Local = new(2026, 11, 21, 1, 2, 3);

    [Fact]
    public void Render_ReplacesAllTokens()
    {
        var name = ObjectNameTemplate.Render(ObjectNameTemplate.DefaultAllTables, "nightly", "DWH", "stage", "Orders", Local, "0001");
        Assert.Equal("exports/DWH/stage.Orders/dt=2026-11-21/Orders_20261121_010203_0001.csv", name);
    }

    [Fact]
    public void Render_SanitizesUnsafeCharacters()
    {
        var name = ObjectNameTemplate.Render("{Name}/{Table}_{Part}.csv", "my job/ç", null, null, "Order Lines", Local, "0001");
        Assert.Equal("my_job__/Order_Lines_0001.csv", name);
    }

    [Fact]
    public void CommonPrefix_StopsBeforeTableSpecificTokens()
    {
        var prefix = ObjectNameTemplate.CommonPrefix(ObjectNameTemplate.DefaultAllTables, "nightly", "DWH", Local);
        Assert.Equal("exports/DWH/", prefix);
    }
}

public class WatermarkTests
{
    [Fact]
    public void DateTime_RoundTripsWithFullPrecision()
    {
        var v = new DateTime(2026, 11, 20, 13, 0, 0, 997).AddTicks(1234);
        var text = Watermark.Serialize(v);
        var p = Watermark.ToParameter("@w", "datetime2", text);
        Assert.Equal(SqlDbType.DateTime2, p.SqlDbType);
        Assert.Equal(v, (DateTime)p.Value);
    }

    [Theory]
    [InlineData("datetime", SqlDbType.DateTime)]      // must NOT become datetime2 (.003 -> .0033333 duplicates)
    [InlineData("smalldatetime", SqlDbType.SmallDateTime)]
    [InlineData("varchar", SqlDbType.VarChar)]        // must NOT become nvarchar (index seek loss)
    [InlineData("nvarchar", SqlDbType.NVarChar)]
    [InlineData("bigint", SqlDbType.BigInt)]
    public void Parameter_UsesTheColumnsRealSqlType(string type, SqlDbType expected)
    {
        var value = type switch
        {
            "datetime" or "smalldatetime" => Watermark.Serialize(new DateTime(2026, 1, 1)),
            "bigint" => "42",
            _ => "ABC"
        };
        Assert.Equal(expected, Watermark.ToParameter("@w", type, value).SqlDbType);
    }

    [Fact]
    public void Rowversion_IsBinary8_AndComparesBigEndian()
    {
        var a = new byte[] { 0, 0, 0, 0, 0, 0, 7, 0xD4 };
        var b = new byte[] { 0, 0, 0, 0, 0, 0, 7, 0xD1 };
        Assert.True(Watermark.Compare(a, b) > 0);
        var p = Watermark.ToParameter("@w", "timestamp", Watermark.Serialize(a));
        Assert.Equal(SqlDbType.Binary, p.SqlDbType);
        Assert.Equal(8, p.Size);
    }

    [Fact]
    public void NoWatermark_IsTypedNull()
    {
        var p = Watermark.ToParameter("@w", null, null);
        Assert.Equal(DBNull.Value, p.Value);
    }

    [Fact]
    public void UnsupportedType_Throws() =>
        Assert.Throws<NotSupportedException>(() => Watermark.NormalizeType("uniqueidentifier"));

    [Fact]
    public void UnsupportedParameterType_ThrowsEnglishMessage()
    {
        var ex = Assert.Throws<NotSupportedException>(() => Watermark.ToParameter("@w", "uniqueidentifier", "x"));
        Assert.Equal("Unsupported watermark type: uniqueidentifier", ex.Message);
    }
}

public class SuggestSchemaTests
{
    [Theory]
    [InlineData("ERP", "erp")]
    [InlineData("Sales DB", "sales_db")]
    [InlineData("  CRM-2024 ", "crm_2024")]
    [InlineData("2024Archive", "db_2024archive")]
    [InlineData("Şirket İş", "sirket_is")]
    [InlineData("", "stage")]
    [InlineData("---", "stage")]
    public void SuggestSchema_FromDatabaseName(string db, string expected) =>
        Assert.Equal(expected, TargetTableManager.SuggestSchema(db));
}

public class SqlTypeMapperTests
{
    [Theory]
    [InlineData("nvarchar", 200, 0, 0, "nvarchar(100)")]   // sys.columns max_length is bytes
    [InlineData("nvarchar", -1, 0, 0, "nvarchar(max)")]
    [InlineData("varchar", 50, 0, 0, "varchar(50)")]
    [InlineData("numeric", 17, 38, 10, "decimal(38,10)")]
    [InlineData("datetime2", 8, 27, 7, "datetime2(7)")]
    [InlineData("timestamp", 8, 0, 0, "binary(8)")]
    [InlineData("int", 4, 10, 0, "int")]
    public void Declaration_FromSysColumns(string type, int maxLength, int precision, int scale, string expected) =>
        Assert.Equal(expected, SqlTypeMapper.Declaration(type, maxLength, precision, scale));
}

public class CsvValueFormatterTests
{
    [Fact]
    public void DateTime_DropsTrailingZeros()
    {
        Assert.Equal("2026-11-20 10:00:00", CsvValueFormatter.FormatDateTime(new DateTime(2026, 11, 20, 10, 0, 0)));
        Assert.Equal("2026-11-20 10:00:00.003", CsvValueFormatter.FormatDateTime(new DateTime(2026, 11, 20, 10, 0, 0, 3)));
    }

    [Fact]
    public void Time_HasNoDanglingDot()
    {
        Assert.Equal("10:15:30.5", CsvValueFormatter.FormatTime(new TimeSpan(0, 10, 15, 30, 500)));
        Assert.Equal("00:00:00", CsvValueFormatter.FormatTime(TimeSpan.Zero));
    }

    [Fact]
    public void Objects_AreCultureInvariant()
    {
        var old = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("tr-TR");
            Assert.Equal("1.5", CsvValueFormatter.FormatObject(1.5m));
            Assert.Equal("0.1", CsvValueFormatter.FormatObject(0.1d));
            Assert.Equal("1", CsvValueFormatter.FormatObject(true));
            Assert.Equal("0x01FF", CsvValueFormatter.FormatObject(new byte[] { 1, 255 }));
        }
        finally { CultureInfo.CurrentCulture = old; }
    }
}

public class PasswordHasherTests
{
    [Fact]
    public void HashVerifies_AndIsSalted()
    {
        var h1 = PasswordHasher.Hash("correct horse battery");
        var h2 = PasswordHasher.Hash("correct horse battery");
        Assert.NotEqual(h1, h2);
        Assert.True(PasswordHasher.Verify("correct horse battery", h1));
        Assert.False(PasswordHasher.Verify("wrong", h1));
        Assert.False(PasswordHasher.Verify("x", null));
    }
}

public class CronPreviewTests
{
    [Fact]
    public void ValidCron_ReturnsNextRunsInJobTimeZone()
    {
        var (ok, _, next) = JobRegistrar.Preview("0 2 * * *", "Europe/Istanbul");
        Assert.True(ok);
        Assert.Equal(5, next.Count);
        Assert.All(next, n => Assert.Equal(2, TimeZoneInfo.ConvertTime(n, TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul")).Hour));
    }

    [Fact]
    public void InvalidCron_IsRejected() => Assert.False(JobRegistrar.Preview("61 * * * *", "UTC").Ok);
}
