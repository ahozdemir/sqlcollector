using System.Data.Common;
using System.Data.SqlTypes;
using System.Numerics;
using Microsoft.Data.SqlClient;
using Parquet;
using Parquet.Data;
using Parquet.Schema;
using SqlDataCollector.Privacy;

namespace SqlDataCollector.Export;

/// <summary>Writes the rows of an export into data files (CSV parts or Parquet files).</summary>
public interface IExportPartWriter : IAsyncDisposable
{
    List<CsvPart> Parts { get; }
    long TotalRows { get; }
    IReadOnlyList<string> Header { get; }
    ValueTask WriteRowAsync(SqlDataReader reader, CancellationToken ct);
    /// <summary>If there are no rows, produces a single file without rows (header / schema only).</summary>
    ValueTask EnsureAtLeastOnePartAsync(CancellationToken ct);
    ValueTask CompleteAsync(CancellationToken ct);
}

/// <summary>Parquet types of the export (SQL type → Parquet logical type); pure, unit tested.</summary>
public static class ParquetTypes
{
    public enum Kind { Bool, Int32, Int64, Float, Double, Decimal, Date, Timestamp, TimestampUtc, String, Binary }

    /// <summary>Up to this precision decimals are written as .NET decimal (exact); above it as BigDecimal (fixed-length bytes).</summary>
    public const int MaxNetDecimalPrecision = 28;

    /// <summary>Kind, and precision/scale for decimals. Every column is nullable.</summary>
    public static (Kind Kind, int Precision, int Scale) Map(string sqlType, int? precision, int? scale) => sqlType.ToLowerInvariant() switch
    {
        "bit" => (Kind.Bool, 0, 0),
        "tinyint" or "smallint" or "int" => (Kind.Int32, 0, 0),
        "bigint" => (Kind.Int64, 0, 0),
        "real" => (Kind.Float, 0, 0),
        "float" => (Kind.Double, 0, 0),
        "decimal" or "numeric" => (Kind.Decimal, precision is > 0 and <= 38 ? precision.Value : 38, scale is >= 0 and <= 38 ? scale.Value : 0),
        "money" => (Kind.Decimal, 19, 4),
        "smallmoney" => (Kind.Decimal, 10, 4),
        "date" => (Kind.Date, 0, 0),
        "datetime" or "datetime2" or "smalldatetime" => (Kind.Timestamp, 0, 0),
        "datetimeoffset" => (Kind.TimestampUtc, 0, 0),
        "binary" or "varbinary" or "image" or "timestamp" or "rowversion" => (Kind.Binary, 0, 0),
        // time: text "hh:mm:ss.fffffff" (readers disagree on the TIME logical type); char, varchar, nvarchar, xml, uniqueidentifier, ...
        _ => (Kind.String, 0, 0)
    };

    /// <summary>Exact unscaled value of a SQL decimal (up to 38 digits, beyond .NET decimal) at the column scale.</summary>
    public static BigDecimal ToBigDecimal(SqlDecimal v, int precision, int scale)
    {
        if (v.Scale != scale) v = SqlDecimal.AdjustScale(v, scale - v.Scale, false);
        var d = v.Data;
        var unscaled = new BigInteger((uint)d[0]) | (new BigInteger((uint)d[1]) << 32) | (new BigInteger((uint)d[2]) << 64) | (new BigInteger((uint)d[3]) << 96);
        return new BigDecimal(v.IsPositive ? unscaled : -unscaled, precision, scale);
    }
}

/// <summary>
/// Streams an export into Parquet files: rows are buffered per column and written as row groups;
/// with MaxRowsPerFile set, output is split into several files. Typed columns (decimal(38) exact via BigDecimal,
/// timestamps in microseconds, dates, binary); masked columns are text.
/// </summary>
public sealed class ParquetPartWriter : IExportPartWriter
{
    private readonly string _directory;
    private readonly string _baseName;
    private readonly ParquetOptions _options;
    private readonly long? _maxRowsPerFile;
    private readonly int _rowGroupSize;
    private readonly Column[] _columns;
    private readonly ColumnMask?[] _masks;
    private readonly PiiSampler? _sampler;
    private readonly ParquetSchema _schema;
    private readonly string[] _header;

    private FileStream? _stream;
    private ParquetWriter? _writer;
    private CsvPart? _current;
    private int _buffered;

    public List<CsvPart> Parts { get; } = [];
    public long TotalRows { get; private set; }
    public IReadOnlyList<string> Header => _header;

    public ParquetPartWriter(string directory, string baseName, CompressionMethod compression, long? maxRowsPerFile, int rowGroupSize,
        SqlDataReader reader, ColumnMask?[]? masks = null, PiiSampler? sampler = null)
    {
        _directory = directory;
        _baseName = baseName;
        _options = new ParquetOptions { CompressionMethod = compression, UseBigDecimal = true };
        _maxRowsPerFile = maxRowsPerFile is > 0 ? maxRowsPerFile : null;
        _rowGroupSize = Math.Clamp(rowGroupSize, 1_000, 1_000_000);
        _masks = masks ?? new ColumnMask?[reader.FieldCount];
        _sampler = sampler;
        var schema = reader.GetColumnSchema();
        _header = schema.Select(c => c.ColumnName).ToArray();
        _columns = schema.Select((c, i) => Column.Create(c, reader.GetDataTypeName(i), masked: _masks[i] is not null)).ToArray();
        _schema = new ParquetSchema(_columns.Select(c => (Field)c.Field));
        Directory.CreateDirectory(directory);
    }

    public async ValueTask WriteRowAsync(SqlDataReader reader, CancellationToken ct)
    {
        if (_writer is null || (_maxRowsPerFile.HasValue && _current!.Rows + _buffered >= _maxRowsPerFile.Value))
            await RotateAsync(ct);

        bool sample = _sampler is { Wants: true };
        for (int i = 0; i < _columns.Length; i++)
        {
            if (reader.IsDBNull(i)) { _columns[i].AddNull(); continue; }
            if (_masks[i] is { } mask)
            {
                // same text as the CSV output, so a hash is identical in both formats
                ((StringColumn)_columns[i]).AddText(mask.Apply(CsvValueFormatter.For(reader.GetDataTypeName(i))(reader, i)));
                continue;
            }
            var value = _columns[i].Add(reader, i, box: sample);
            if (sample) _sampler!.Add(i, value is null ? null : Masker.ToText(value));
        }
        _sampler?.EndRow();
        TotalRows++;
        if (++_buffered >= _rowGroupSize) await FlushRowGroupAsync(ct);
    }

    public async ValueTask EnsureAtLeastOnePartAsync(CancellationToken ct)
    {
        if (Parts.Count == 0) await RotateAsync(ct);
    }

    public async ValueTask CompleteAsync(CancellationToken ct) => await CloseCurrentAsync(ct);

    private async Task RotateAsync(CancellationToken ct)
    {
        await CloseCurrentAsync(ct);
        int number = Parts.Count + 1;
        var path = Path.Combine(_directory, $"{_baseName}_part{number:0000}.parquet");
        _stream = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 20, useAsync: true);
        _writer = await ParquetWriter.CreateAsync(_schema, _stream, _options, append: false, ct);
        _current = new CsvPart { Number = number, LocalPath = path };
        Parts.Add(_current);
    }

    private async Task FlushRowGroupAsync(CancellationToken ct)
    {
        if (_buffered == 0 || _writer is null) return;
        using (var rg = _writer.CreateRowGroup())
        {
            foreach (var c in _columns) await c.WriteAsync(rg, ct);
        }
        foreach (var c in _columns) c.Clear();
        _current!.Rows += _buffered;
        _buffered = 0;
    }

    private async Task CloseCurrentAsync(CancellationToken ct)
    {
        if (_writer is null) return;
        await FlushRowGroupAsync(ct);
        await _writer.DisposeAsync();
        _writer = null;
        await _stream!.DisposeAsync();
        _stream = null;
        _current!.Bytes = new FileInfo(_current.LocalPath).Length;
    }

    public async ValueTask DisposeAsync()
    {
        if (_writer is not null) await _writer.DisposeAsync();
        if (_stream is not null) await _stream.DisposeAsync();
        _writer = null; _stream = null;
    }

    // ------------------------------------------------------------------ column buffers

    private abstract class Column(DataField field)
    {
        public DataField Field { get; } = field;
        /// <summary>Reads the value once (sequential access) into the buffer; returns it boxed when asked (guard sampling).</summary>
        public abstract object? Add(SqlDataReader r, int i, bool box);
        public abstract void AddNull();
        public abstract Task WriteAsync(ParquetRowGroupWriter rg, CancellationToken ct);
        public abstract void Clear();

        public static Column Create(DbColumn c, string sqlType, bool masked)
        {
            var name = c.ColumnName;
            if (masked) return new StringColumn(new DataField<string>(name, true), (r, i) => r.GetString(i));
            var (kind, p, s) = ParquetTypes.Map(sqlType, c.NumericPrecision, c.NumericScale);
            var t = sqlType.ToLowerInvariant();
            return kind switch
            {
                ParquetTypes.Kind.Bool => new StructColumn<bool>(new DataField<bool?>(name, true), (r, i) => r.GetBoolean(i)),
                ParquetTypes.Kind.Int32 => new StructColumn<int>(new DataField<int?>(name, true), t switch
                {
                    "tinyint" => (r, i) => r.GetByte(i),
                    "smallint" => (r, i) => r.GetInt16(i),
                    _ => (r, i) => r.GetInt32(i)
                }),
                ParquetTypes.Kind.Int64 => new StructColumn<long>(new DataField<long?>(name, true), (r, i) => r.GetInt64(i)),
                ParquetTypes.Kind.Float => new StructColumn<float>(new DataField<float?>(name, true), (r, i) => r.GetFloat(i)),
                ParquetTypes.Kind.Double => new StructColumn<double>(new DataField<double?>(name, true), (r, i) => r.GetDouble(i)),
                ParquetTypes.Kind.Decimal when p <= ParquetTypes.MaxNetDecimalPrecision => new StructColumn<decimal>(new DecimalDataField(name, p, s, false, true),
                    t is "money" or "smallmoney" ? (r, i) => r.GetDecimal(i) : (r, i) => r.GetSqlDecimal(i).Value),
                ParquetTypes.Kind.Decimal => new DecimalColumn(new DecimalDataField(name, p, s, true, true, null, null, typeof(BigDecimal)), p, s,
                    t is "money" or "smallmoney"),
                ParquetTypes.Kind.Date => new StructColumn<DateTime>(new DateTimeDataField(name, DateTimeFormat.Date, false, null, true), (r, i) => r.GetDateTime(i)),
                ParquetTypes.Kind.Timestamp => new StructColumn<DateTime>(
                    new DateTimeDataField(name, DateTimeFormat.Timestamp, false, DateTimeTimeUnit.Micros, true), (r, i) => r.GetDateTime(i)),
                ParquetTypes.Kind.TimestampUtc => new StructColumn<DateTime>(
                    new DateTimeDataField(name, DateTimeFormat.Timestamp, true, DateTimeTimeUnit.Micros, true), (r, i) => r.GetDateTimeOffset(i).UtcDateTime),
                ParquetTypes.Kind.Binary => new BinaryColumn(new DataField<byte[]>(name, true)),
                _ => new StringColumn(new DataField<string>(name, true), t switch
                {
                    "uniqueidentifier" => (r, i) => r.GetGuid(i).ToString("D"),
                    "time" => (r, i) => CsvValueFormatter.FormatTime(r.GetTimeSpan(i)),
                    "char" or "varchar" or "nchar" or "nvarchar" or "text" or "ntext" or "xml" => (r, i) => r.GetString(i),
                    _ => (r, i) => Masker.ToText(r.GetValue(i))
                })
            };
        }
    }

    private sealed class StructColumn<T>(DataField field, Func<SqlDataReader, int, T> get) : Column(field) where T : struct
    {
        private readonly List<T?> _values = [];
        public override object? Add(SqlDataReader r, int i, bool box) { var v = get(r, i); _values.Add(v); return box ? v : null; }
        public override void AddNull() => _values.Add(null);
        public override async Task WriteAsync(ParquetRowGroupWriter rg, CancellationToken ct) =>
            await rg.WriteAsync<T>(Field, _values.ToArray(), null, null, ct);
        public override void Clear() => _values.Clear();
    }

    private sealed class DecimalColumn(DataField field, int precision, int scale, bool money) : Column(field)
    {
        private readonly List<BigDecimal?> _values = [];
        public override object? Add(SqlDataReader r, int i, bool box)
        {
            var sql = money ? new SqlDecimal(r.GetDecimal(i)) : r.GetSqlDecimal(i);
            _values.Add(ParquetTypes.ToBigDecimal(sql, precision, scale));
            return box ? sql : null;
        }
        public override void AddNull() => _values.Add(null);
        public override async Task WriteAsync(ParquetRowGroupWriter rg, CancellationToken ct) =>
            await rg.WriteAsync<BigDecimal>(Field, _values.ToArray(), null, null, ct);
        public override void Clear() => _values.Clear();
    }

    private sealed class StringColumn(DataField field, Func<SqlDataReader, int, string> get) : Column(field)
    {
        private readonly List<string?> _values = [];
        public override object? Add(SqlDataReader r, int i, bool box) { var v = get(r, i); _values.Add(v); return v; }
        public void AddText(string? v) => _values.Add(v);
        public override void AddNull() => _values.Add(null);
        public override async Task WriteAsync(ParquetRowGroupWriter rg, CancellationToken ct) => await rg.WriteAsync(Field, _values!, null);
        public override void Clear() => _values.Clear();
    }

    private sealed class BinaryColumn(DataField field) : Column(field)
    {
        private readonly List<byte[]?> _values = [];
        public override object? Add(SqlDataReader r, int i, bool box) { var v = r.GetSqlBinary(i).Value; _values.Add(v); return box ? v : null; }
        public override void AddNull() => _values.Add(null);
        public override async Task WriteAsync(ParquetRowGroupWriter rg, CancellationToken ct) => await rg.WriteAsync(Field, _values!, null);
        public override void Clear() => _values.Clear();
    }
}
