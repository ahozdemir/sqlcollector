using System.Globalization;
using System.IO.Compression;
using System.Text;
using Microsoft.Data.SqlClient;
using SqlDataCollector.Models;

namespace SqlDataCollector.Export;

public sealed record CsvFormat(string Delimiter, QuoteMode QuoteMode, string NullValue, string NewLine, bool Utf8Bom, bool Gzip);

public sealed class CsvPart
{
    public int Number { get; init; }
    public string LocalPath { get; init; } = "";
    public long Rows { get; set; }
    public long Bytes { get; set; }
    public string ObjectName { get; set; } = "";
}

/// <summary>
/// RFC 4180 compliant streaming CSV writer. With MaxRowsPerFile set
/// it splits output into parts; each part starts with the header row.
/// </summary>
public sealed class CsvPartWriter : IDisposable
{
    private readonly string _directory;
    private readonly string _baseName;
    private readonly CsvFormat _format;
    private readonly long? _maxRowsPerFile;
    private readonly string[] _header;
    private readonly Func<SqlDataReader, int, string>[] _formatters;
    private readonly bool[] _isText;
    private readonly char[] _specialChars;
    private readonly Privacy.ColumnMask?[] _masks;
    private readonly Privacy.PiiSampler? _sampler;

    private StreamWriter? _writer;
    private CsvPart? _current;

    public List<CsvPart> Parts { get; } = [];
    public long TotalRows { get; private set; }

    /// <param name="masks">Per column: the masking applied to the value (null = written as is).</param>
    /// <param name="sampler">Collects the first unmasked values for the data protection guard.</param>
    public CsvPartWriter(string directory, string baseName, CsvFormat format, long? maxRowsPerFile, SqlDataReader reader,
        Privacy.ColumnMask?[]? masks = null, Privacy.PiiSampler? sampler = null)
    {
        _directory = directory;
        _baseName = baseName;
        _format = format;
        _maxRowsPerFile = maxRowsPerFile is > 0 ? maxRowsPerFile : null;
        _header = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToArray();
        _formatters = Enumerable.Range(0, reader.FieldCount).Select(i => CsvValueFormatter.For(reader.GetDataTypeName(i))).ToArray();
        _masks = masks ?? new Privacy.ColumnMask?[reader.FieldCount];
        _sampler = sampler;
        // a masked value is text
        _isText = Enumerable.Range(0, reader.FieldCount).Select(i => reader.GetFieldType(i) == typeof(string) || _masks[i] is not null).ToArray();
        _specialChars = (format.Delimiter + "\"\r\n").Distinct().ToArray();
        Directory.CreateDirectory(directory);
    }

    public IReadOnlyList<string> Header => _header;

    public void WriteRow(SqlDataReader reader)
    {
        if (_writer is null || (_maxRowsPerFile.HasValue && _current!.Rows >= _maxRowsPerFile.Value))
            Rotate();

        var w = _writer!;
        for (int i = 0; i < _header.Length; i++)
        {
            if (i > 0) w.Write(_format.Delimiter);
            if (reader.IsDBNull(i)) { w.Write(_format.NullValue); continue; }
            var value = _formatters[i](reader, i);
            if (_masks[i] is { } mask)
            {
                var masked = mask.Apply(value);
                if (masked is null) { w.Write(_format.NullValue); continue; }
                value = masked;
            }
            else if (_sampler is { Wants: true }) _sampler.Add(i, value);
            WriteField(w, value, _isText[i]);
        }
        _sampler?.EndRow();
        w.Write(_format.NewLine);
        _current!.Rows++;
        TotalRows++;
    }

    /// <summary>If there are no rows, produces a single header-only file.</summary>
    public void EnsureAtLeastOnePart()
    {
        if (Parts.Count == 0) Rotate();
    }

    public void Complete() => CloseCurrent();

    private void WriteField(StreamWriter w, string value, bool isText)
    {
        bool quote = _format.QuoteMode switch
        {
            QuoteMode.All => true,
            QuoteMode.AllText when isText => true,
            _ => value.IndexOfAny(_specialChars) >= 0
                 || (value.Length > 0 && (char.IsWhiteSpace(value[0]) || char.IsWhiteSpace(value[^1])))
                 || (value.Length == 0 && _format.NullValue.Length == 0) // keep empty string distinct from NULL
        };
        if (!quote) { w.Write(value); return; }
        w.Write('"');
        w.Write(value.Replace("\"", "\"\""));
        w.Write('"');
    }

    private void Rotate()
    {
        CloseCurrent();
        int number = Parts.Count + 1;
        var path = Path.Combine(_directory, $"{_baseName}_part{number:0000}.csv{(_format.Gzip ? ".gz" : "")}");
        Stream stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
        if (_format.Gzip) stream = new GZipStream(stream, CompressionLevel.Optimal);
        _writer = new StreamWriter(stream, new UTF8Encoding(_format.Utf8Bom), 1 << 16);
        _current = new CsvPart { Number = number, LocalPath = path };
        Parts.Add(_current);

        for (int i = 0; i < _header.Length; i++)
        {
            if (i > 0) _writer.Write(_format.Delimiter);
            WriteField(_writer, _header[i], isText: true);
        }
        _writer.Write(_format.NewLine);
    }

    private void CloseCurrent()
    {
        if (_writer is null) return;
        _writer.Flush();
        _writer.Dispose();
        _writer = null;
        _current!.Bytes = new FileInfo(_current.LocalPath).Length;
    }

    public void Dispose() => CloseCurrent();
}

/// <summary>Culture-invariant value formatting by SQL type.</summary>
public static class CsvValueFormatter
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static Func<SqlDataReader, int, string> For(string dataTypeName) => dataTypeName.ToLowerInvariant() switch
    {
        "date" => (r, i) => r.GetDateTime(i).ToString("yyyy-MM-dd", Inv),
        "datetime" or "datetime2" or "smalldatetime" => (r, i) => FormatDateTime(r.GetDateTime(i)),
        "datetimeoffset" => (r, i) => FormatDateTimeOffset(r.GetDateTimeOffset(i)),
        "time" => (r, i) => FormatTime(r.GetTimeSpan(i)),
        "bit" => (r, i) => r.GetBoolean(i) ? "1" : "0",
        "binary" or "varbinary" or "image" or "timestamp" or "rowversion" => (r, i) => "0x" + Convert.ToHexString(r.GetSqlBinary(i).Value),
        "uniqueidentifier" => (r, i) => r.GetGuid(i).ToString("D"),
        "float" => (r, i) => r.GetDouble(i).ToString("R", Inv),
        "real" => (r, i) => r.GetFloat(i).ToString("R", Inv),
        // decimal(38,x) can exceed .NET decimal -> SqlDecimal
        "decimal" or "numeric" => (r, i) => r.GetSqlDecimal(i).ToString(),
        "money" or "smallmoney" => (r, i) => r.GetDecimal(i).ToString(Inv),
        _ => (r, i) => FormatObject(r.GetValue(i))
    };

    public static string FormatDateTime(DateTime v) =>
        v.Ticks % TimeSpan.TicksPerSecond == 0
            ? v.ToString("yyyy-MM-dd HH:mm:ss", Inv)
            : v.ToString("yyyy-MM-dd HH:mm:ss.fffffff", Inv).TrimEnd('0');

    public static string FormatDateTimeOffset(DateTimeOffset v) =>
        FormatDateTime(v.DateTime) + v.ToString("zzz", Inv);

    public static string FormatTime(TimeSpan v)
    {
        var s = v.ToString(@"hh\:mm\:ss", Inv);
        long frac = v.Ticks % TimeSpan.TicksPerSecond;
        return frac == 0 ? s : s + "." + frac.ToString("0000000", Inv).TrimEnd('0');
    }

    public static string FormatObject(object v) => v switch
    {
        string s => s,
        DateTime dt => FormatDateTime(dt),
        DateTimeOffset dto => FormatDateTimeOffset(dto),
        TimeSpan ts => FormatTime(ts),
        bool b => b ? "1" : "0",
        byte[] bytes => "0x" + Convert.ToHexString(bytes),
        Guid g => g.ToString("D"),
        double d => d.ToString("R", Inv),
        float f => f.ToString("R", Inv),
        IFormattable f => f.ToString(null, Inv),
        _ => v.ToString() ?? ""
    };
}
