using System.Data.SqlTypes;
using System.Numerics;
using SqlDataCollector.Export;
using SqlDataCollector.Models;
using static SqlDataCollector.Export.ParquetTypes;

namespace SqlDataCollector.Tests.Unit;

public class ParquetTypesTests
{
    [Theory]
    [InlineData("bit", Kind.Bool)]
    [InlineData("tinyint", Kind.Int32)]
    [InlineData("smallint", Kind.Int32)]
    [InlineData("INT", Kind.Int32)]
    [InlineData("bigint", Kind.Int64)]
    [InlineData("real", Kind.Float)]
    [InlineData("float", Kind.Double)]
    [InlineData("date", Kind.Date)]
    [InlineData("datetime2", Kind.Timestamp)]
    [InlineData("datetimeoffset", Kind.TimestampUtc)]
    [InlineData("time", Kind.String)]
    [InlineData("varbinary", Kind.Binary)]
    [InlineData("rowversion", Kind.Binary)]
    [InlineData("uniqueidentifier", Kind.String)]
    [InlineData("xml", Kind.String)]
    [InlineData("nvarchar", Kind.String)]
    public void Map_Kinds(string sqlType, Kind expected) => Assert.Equal(expected, Map(sqlType, null, null).Kind);

    [Fact]
    public void Map_Decimals_KeepPrecisionAndScale()
    {
        Assert.Equal((Kind.Decimal, 38, 10), Map("decimal", 38, 10));
        Assert.Equal((Kind.Decimal, 19, 4), Map("money", null, null));
        Assert.Equal((Kind.Decimal, 10, 4), Map("smallmoney", null, null));
    }

    [Theory]
    [InlineData("12345678901234567890.1234567890", 38, 10, "123456789012345678901234567890")]
    [InlineData("-0.5", 18, 2, "-50")]
    [InlineData("99999999999999999999999999999999999999", 38, 0, "99999999999999999999999999999999999999")]
    public void ToBigDecimal_IsExact(string value, int precision, int scale, string unscaled)
    {
        var big = ToBigDecimal(SqlDecimal.Parse(value), precision, scale);
        Assert.Equal(BigInteger.Parse(unscaled), big.UnscaledValue);
        Assert.Equal(scale, big.Scale);
    }

    [Theory]
    [InlineData("exports/a/t_0001.csv", ExportFileFormat.Parquet, false, "exports/a/t_0001.parquet")]
    [InlineData("exports/a/t_0001.csv.gz", ExportFileFormat.Parquet, true, "exports/a/t_0001.parquet")]
    [InlineData("exports/a/t_0001", ExportFileFormat.Parquet, false, "exports/a/t_0001.parquet")]
    [InlineData("exports/a/t_0001.csv", ExportFileFormat.Csv, true, "exports/a/t_0001.csv.gz")]
    [InlineData("exports/a/t_0001.csv", ExportFileFormat.Csv, false, "exports/a/t_0001.csv")]
    public void ObjectName_Extension(string rendered, ExportFileFormat format, bool gzip, string expected) =>
        Assert.Equal(expected, ObjectNameTemplate.WithExtension(rendered, new ExportJobDef { FileFormat = format, Gzip = gzip }));
}
