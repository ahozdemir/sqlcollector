using System.Data;
using System.Globalization;
using Microsoft.Data.SqlClient;

namespace SqlDataCollector.Extract;

/// <summary>
/// Stores the watermark as text and converts it to a parameter of the source column's REAL SQL type.
/// E.g. comparing a datetime column with a datetime2 parameter
/// (.003 -> .0033333) re-reads the same rows on every run;
/// comparing a varchar column with an nvarchar parameter loses the index seek.
/// </summary>
public static class Watermark
{
    private const string DtFormat = "yyyy-MM-dd'T'HH:mm:ss.fffffff";
    private const string DtoFormat = "yyyy-MM-dd'T'HH:mm:ss.fffffffzzz";

    public static readonly HashSet<string> SupportedTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "datetime", "datetime2", "smalldatetime", "date", "datetimeoffset",
        "bigint", "int", "smallint", "tinyint", "decimal", "numeric",
        "timestamp", "rowversion", "binary", "varbinary",
        "varchar", "nvarchar", "char", "nchar"
    };

    public static string NormalizeType(string? dataTypeName)
    {
        var t = (dataTypeName ?? "").ToLowerInvariant();
        if (!SupportedTypes.Contains(t))
            throw new NotSupportedException($"Unsupported watermark column type: '{dataTypeName}'. Use date/number/rowversion columns.");
        return t;
    }

    public static string Serialize(object value) => value switch
    {
        DateTime dt => dt.ToString(DtFormat, CultureInfo.InvariantCulture),
        DateTimeOffset dto => dto.ToString(DtoFormat, CultureInfo.InvariantCulture),
        byte[] b => Convert.ToHexString(b),
        System.Data.SqlTypes.SqlDecimal sd => sd.ToString(),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? ""
    };

    public static SqlParameter ToParameter(string name, string? type, string? value)
    {
        if (type is null || value is null)
            return new SqlParameter(name, SqlDbType.NVarChar, 100) { Value = DBNull.Value };

        var inv = CultureInfo.InvariantCulture;
        return type.ToLowerInvariant() switch
        {
            "datetime" => new SqlParameter(name, SqlDbType.DateTime) { Value = ParseDt(value) },
            "smalldatetime" => new SqlParameter(name, SqlDbType.SmallDateTime) { Value = ParseDt(value) },
            "date" => new SqlParameter(name, SqlDbType.Date) { Value = ParseDt(value).Date },
            "datetime2" => new SqlParameter(name, SqlDbType.DateTime2) { Scale = 7, Value = ParseDt(value) },
            "datetimeoffset" => new SqlParameter(name, SqlDbType.DateTimeOffset) { Scale = 7, Value = DateTimeOffset.ParseExact(value, DtoFormat, inv) },
            "bigint" => new SqlParameter(name, SqlDbType.BigInt) { Value = long.Parse(value, inv) },
            "int" => new SqlParameter(name, SqlDbType.Int) { Value = int.Parse(value, inv) },
            "smallint" => new SqlParameter(name, SqlDbType.SmallInt) { Value = short.Parse(value, inv) },
            "tinyint" => new SqlParameter(name, SqlDbType.TinyInt) { Value = byte.Parse(value, inv) },
            "decimal" or "numeric" => DecimalParam(name, value),
            "timestamp" or "rowversion" or "binary" => new SqlParameter(name, SqlDbType.Binary, value.Length / 2) { Value = Convert.FromHexString(value) },
            "varbinary" => new SqlParameter(name, SqlDbType.VarBinary, Math.Max(1, value.Length / 2)) { Value = Convert.FromHexString(value) },
            "varchar" or "char" => new SqlParameter(name, SqlDbType.VarChar, Math.Max(1, value.Length)) { Value = value },
            "nvarchar" or "nchar" => new SqlParameter(name, SqlDbType.NVarChar, Math.Max(1, value.Length)) { Value = value },
            _ => throw new NotSupportedException($"Unsupported watermark type: {type}")
        };
    }

    private static DateTime ParseDt(string v) => DateTime.ParseExact(v, DtFormat, CultureInfo.InvariantCulture);

    private static SqlParameter DecimalParam(string name, string value)
    {
        var sd = System.Data.SqlTypes.SqlDecimal.Parse(value);
        return new SqlParameter(name, SqlDbType.Decimal) { Precision = 38, Scale = sd.Scale, SqlValue = sd };
    }

    /// <summary>Comparison used to find the maximum value read.</summary>
    public static int Compare(object a, object b)
    {
        if (a is byte[] x && b is byte[] y)
        {
            // rowversion/binary: big-endian, same as SQL Server binary comparison
            int len = Math.Min(x.Length, y.Length);
            for (int i = 0; i < len; i++)
                if (x[i] != y[i]) return x[i].CompareTo(y[i]);
            return x.Length.CompareTo(y.Length);
        }
        if (a is string sa && b is string sb) return string.CompareOrdinal(sa, sb);
        return ((IComparable)a).CompareTo(b);
    }
}
