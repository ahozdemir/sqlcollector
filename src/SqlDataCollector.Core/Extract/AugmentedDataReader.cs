using System.Collections;
using System.Data.Common;
using Microsoft.Data.SqlClient;
using SqlDataCollector.Infrastructure;

namespace SqlDataCollector.Extract;

/// <summary>
/// Wraps the source reader and appends 4 constant technical columns
/// (_LoadId, _LoadedAt, _ExtractJobId, _SourceServer), so the source query
/// runs unchanged (CTEs, temp tables, EXEC sp ... are all supported).
/// It also tracks the maximum watermark value while streaming.
/// </summary>
public sealed class AugmentedDataReader : DbDataReader
{
    private readonly DbDataReader _inner;
    private readonly int _baseCount;
    private readonly object[] _extraValues;
    private readonly int? _watermarkOrdinal;
    private readonly SqlDataReader? _sql;
    private readonly bool[] _isDecimal;

    private static readonly string[] ExtraNames = SqlIdent.TechnicalColumns;
    private static readonly Type[] ExtraTypes = [typeof(long), typeof(DateTime), typeof(int), typeof(string)];
    private static readonly string[] ExtraSqlTypes = ["bigint", "datetime2", "int", "nvarchar"];

    public long RowsRead { get; private set; }
    public object? MaxWatermark { get; private set; }

    public AugmentedDataReader(DbDataReader inner, long loadId, DateTime loadedAtUtc, int extractJobId, string sourceServer, int? watermarkOrdinal)
    {
        _inner = inner;
        _baseCount = inner.FieldCount;
        _extraValues = [loadId, loadedAtUtc, extractJobId, sourceServer];
        _watermarkOrdinal = watermarkOrdinal;
        _sql = inner as SqlDataReader;
        // decimal(38,x) values can exceed .NET decimal (28-29 digits) -> pass as SqlDecimal
        _isDecimal = Enumerable.Range(0, _baseCount)
            .Select(i => _sql is not null && inner.GetDataTypeName(i).ToLowerInvariant() is "decimal" or "numeric")
            .ToArray();
    }

    private bool IsExtra(int i) => i >= _baseCount;
    private int Ex(int i) => i - _baseCount;

    public override bool Read()
    {
        if (!_inner.Read()) return false;
        RowsRead++;
        if (_watermarkOrdinal is int o && !_inner.IsDBNull(o))
        {
            var v = GetValue(o);
            if (MaxWatermark is null || Watermark.Compare(v, MaxWatermark) > 0) MaxWatermark = v;
        }
        return true;
    }

    public override Task<bool> ReadAsync(CancellationToken cancellationToken) => Task.FromResult(Read());

    public override int FieldCount => _baseCount + ExtraNames.Length;
    public override int VisibleFieldCount => FieldCount;
    public override object this[int ordinal] => GetValue(ordinal);
    public override object this[string name] => GetValue(GetOrdinal(name));
    public override int Depth => _inner.Depth;
    public override bool HasRows => _inner.HasRows;
    public override bool IsClosed => _inner.IsClosed;
    public override int RecordsAffected => _inner.RecordsAffected;

    public override string GetName(int ordinal) => IsExtra(ordinal) ? ExtraNames[Ex(ordinal)] : _inner.GetName(ordinal);
    public override string GetDataTypeName(int ordinal) => IsExtra(ordinal) ? ExtraSqlTypes[Ex(ordinal)] : _inner.GetDataTypeName(ordinal);
    public override Type GetFieldType(int ordinal) => IsExtra(ordinal) ? ExtraTypes[Ex(ordinal)] : _inner.GetFieldType(ordinal);

    public override int GetOrdinal(string name)
    {
        for (int i = 0; i < ExtraNames.Length; i++)
            if (string.Equals(ExtraNames[i], name, StringComparison.OrdinalIgnoreCase)) return _baseCount + i;
        return _inner.GetOrdinal(name);
    }

    public override object GetValue(int ordinal)
    {
        if (IsExtra(ordinal)) return _extraValues[Ex(ordinal)];
        if (_isDecimal[ordinal])
            return _sql!.IsDBNull(ordinal) ? DBNull.Value : _sql.GetSqlDecimal(ordinal);
        return _inner.GetValue(ordinal);
    }

    public override int GetValues(object[] values)
    {
        int n = Math.Min(values.Length, FieldCount);
        for (int i = 0; i < n; i++) values[i] = GetValue(i);
        return n;
    }

    public override bool IsDBNull(int ordinal) => !IsExtra(ordinal) && _inner.IsDBNull(ordinal);

    public override bool GetBoolean(int ordinal) => IsExtra(ordinal) ? (bool)GetValue(ordinal) : _inner.GetBoolean(ordinal);
    public override byte GetByte(int ordinal) => IsExtra(ordinal) ? Convert.ToByte(GetValue(ordinal)) : _inner.GetByte(ordinal);
    public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) =>
        IsExtra(ordinal) ? throw new NotSupportedException() : _inner.GetBytes(ordinal, dataOffset, buffer, bufferOffset, length);
    public override char GetChar(int ordinal) => IsExtra(ordinal) ? Convert.ToChar(GetValue(ordinal)) : _inner.GetChar(ordinal);
    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length) =>
        IsExtra(ordinal) ? throw new NotSupportedException() : _inner.GetChars(ordinal, dataOffset, buffer, bufferOffset, length);
    public override DateTime GetDateTime(int ordinal) => IsExtra(ordinal) ? (DateTime)GetValue(ordinal) : _inner.GetDateTime(ordinal);
    public override decimal GetDecimal(int ordinal) => IsExtra(ordinal) ? Convert.ToDecimal(GetValue(ordinal)) : _inner.GetDecimal(ordinal);
    public override double GetDouble(int ordinal) => IsExtra(ordinal) ? Convert.ToDouble(GetValue(ordinal)) : _inner.GetDouble(ordinal);
    public override float GetFloat(int ordinal) => IsExtra(ordinal) ? Convert.ToSingle(GetValue(ordinal)) : _inner.GetFloat(ordinal);
    public override Guid GetGuid(int ordinal) => IsExtra(ordinal) ? (Guid)GetValue(ordinal) : _inner.GetGuid(ordinal);
    public override short GetInt16(int ordinal) => IsExtra(ordinal) ? Convert.ToInt16(GetValue(ordinal)) : _inner.GetInt16(ordinal);
    public override int GetInt32(int ordinal) => IsExtra(ordinal) ? Convert.ToInt32(GetValue(ordinal)) : _inner.GetInt32(ordinal);
    public override long GetInt64(int ordinal) => IsExtra(ordinal) ? Convert.ToInt64(GetValue(ordinal)) : _inner.GetInt64(ordinal);
    public override string GetString(int ordinal) => IsExtra(ordinal) ? (string)GetValue(ordinal) : _inner.GetString(ordinal);

    public override bool NextResult() => false;
    public override IEnumerator GetEnumerator() => new DbEnumerator(this, closeReader: false);
    public override void Close() => _inner.Close();

    protected override void Dispose(bool disposing)
    {
        if (disposing) _inner.Dispose();
        base.Dispose(disposing);
    }
}
