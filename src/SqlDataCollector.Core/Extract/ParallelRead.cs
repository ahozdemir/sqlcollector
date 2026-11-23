using System.Collections;
using System.Collections.ObjectModel;
using System.Data;
using System.Data.Common;
using System.Runtime.ExceptionServices;
using System.Threading.Channels;
using Microsoft.Data.SqlClient;
using SqlDataCollector.Infrastructure;
using SqlDataCollector.Models;

namespace SqlDataCollector.Extract;

/// <summary>
/// One slice of a parallel table read: the rows whose split column value is in [From, To). The first slice also takes
/// NULLs and everything below, the last everything above – so rows outside the measured MIN/MAX are never lost.
/// </summary>
public sealed record SourceSlice(string Column, string SqlType, object? From, object? To)
{
    public const string FromParam = "@SliceFrom";
    public const string ToParam = "@SliceTo";

    public string Predicate()
    {
        var c = SqlIdent.Quote(Column);
        return (From, To) switch
        {
            (null, null) => "1 = 1",
            (null, _) => $"({c} < {ToParam} OR {c} IS NULL)",
            (_, null) => $"{c} >= {FromParam}",
            _ => $"{c} >= {FromParam} AND {c} < {ToParam}"
        };
    }

    /// <summary>The bounds as parameters of the column's real SQL type (no implicit conversion, index seeks stay possible).</summary>
    public IEnumerable<SqlParameter> Parameters()
    {
        if (From is not null) yield return Watermark.ToParameter(FromParam, SqlType, Watermark.Serialize(From));
        if (To is not null) yield return Watermark.ToParameter(ToParam, SqlType, Watermark.Serialize(To));
    }
}

/// <summary>Splits the value range of a column into equal slices (pure; unit tested).</summary>
public static class SourceSplitter
{
    /// <summary>Column types a table read can be split on.</summary>
    public static readonly IReadOnlySet<string> SplitTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "tinyint", "smallint", "int", "bigint", "date", "smalldatetime", "datetime", "datetime2"
    };

    private static readonly HashSet<string> IntegerTypes = new(StringComparer.OrdinalIgnoreCase) { "tinyint", "smallint", "int", "bigint" };

    /// <summary>The slices for <paramref name="readers"/> readers; empty when one reader is enough (empty table, tiny range).</summary>
    public static IReadOnlyList<SourceSlice> Slices(string column, string sqlType, object? min, object? max, int readers)
    {
        var bounds = Boundaries(sqlType, min, max, readers);
        if (bounds.Count == 0) return [];
        var slices = new List<SourceSlice>(bounds.Count + 1);
        for (int i = 0; i <= bounds.Count; i++)
            slices.Add(new SourceSlice(column, sqlType.ToLowerInvariant(), i == 0 ? null : bounds[i - 1], i == bounds.Count ? null : bounds[i]));
        return slices;
    }

    /// <summary>Ascending, distinct inner boundaries (readers - 1 of them at most) between MIN and MAX.</summary>
    public static IReadOnlyList<object> Boundaries(string sqlType, object? min, object? max, int readers)
    {
        if (readers < 2 || min is null or DBNull || max is null or DBNull) return [];
        var type = sqlType.ToLowerInvariant();
        if (!SplitTypes.Contains(type)) throw new NotSupportedException($"A table read cannot be split on a {sqlType} column.");
        var list = new List<object>();

        if (IntegerTypes.Contains(type))
        {
            long lo = Convert.ToInt64(min), hi = Convert.ToInt64(max);
            if (hi < lo) return [];
            Int128 span = (Int128)hi - lo + 1;
            var k = (int)Int128.Min(readers, span);
            for (int i = 1; i < k; i++) list.Add((long)(lo + span * i / k));
        }
        else if (type == "date")
        {
            DateTime lo = ((DateTime)min).Date, hi = ((DateTime)max).Date;
            if (hi < lo) return [];
            long days = (long)(hi - lo).TotalDays + 1;
            var k = (int)Math.Min(readers, days);
            for (int i = 1; i < k; i++) list.Add(lo.AddDays(days * i / k));
        }
        else
        {
            DateTime lo = (DateTime)min, hi = (DateTime)max;
            if (hi < lo) return [];
            Int128 span = (Int128)hi.Ticks - lo.Ticks + 1;
            // smalldatetime has whole minutes and datetime 1/300 s: slices narrower than that would only be empty
            Int128 step = type switch { "smalldatetime" => TimeSpan.TicksPerMinute, "datetime" => TimeSpan.TicksPerMillisecond * 10, _ => 1 };
            var k = (int)Int128.Min(readers, Int128.Max(1, span / step));
            for (int i = 1; i < k; i++) list.Add(new DateTime((long)(lo.Ticks + span * i / k), lo.Kind));
        }
        return list.Distinct().ToList();
    }
}

/// <summary>Column names and types of a row stream (taken from the reader that produces it).</summary>
public sealed record RowShape(string[] Names, Type[] Types, string[] TypeNames)
{
    public static RowShape Of(DbDataReader r)
    {
        int n = r.FieldCount;
        var shape = new RowShape(new string[n], new Type[n], new string[n]);
        for (int i = 0; i < n; i++) { shape.Names[i] = r.GetName(i); shape.Types[i] = r.GetFieldType(i); shape.TypeNames[i] = r.GetDataTypeName(i); }
        return shape;
    }
}

/// <summary>
/// A forward-only reader over batches of rows (object[]) arriving through a channel – what a bulk copy reads when rows
/// are produced by other threads. A failure of the producer completes the channel with the error; Read rethrows it.
/// Values are passed on as they were put in (decimal as SqlDecimal, masked values as text).
/// </summary>
public class RowBufferReader : DbDataReader
{
    private readonly ChannelReader<List<object[]>> _input;
    private readonly RowShape _shape;
    private List<object[]>? _batch;
    private int _pos;
    private object[]? _row;

    public RowBufferReader(ChannelReader<List<object[]>> input, RowShape shape)
    {
        _input = input;
        _shape = shape;
    }

    /// <summary>Rows handed out by Read.</summary>
    public long RowsRead { get; private set; }

    protected bool Disposed { get; set; }

    public override bool Read()
    {
        while (true)
        {
            if (_batch is not null && ++_pos < _batch.Count) { _row = _batch[_pos]; RowsRead++; return true; }
            _batch = null;
            if (_input.TryRead(out var next)) { _batch = next; _pos = -1; continue; }
            bool more;
            try { more = _input.WaitToReadAsync().AsTask().GetAwaiter().GetResult(); }
            catch (ChannelClosedException e) when (e.InnerException is not null) { ExceptionDispatchInfo.Throw(e.InnerException); throw; }
            if (!more) { _row = null; return false; }
        }
    }

    public override Task<bool> ReadAsync(CancellationToken cancellationToken) => Task.FromResult(Read());

    private object[] Row => _row ?? throw new InvalidOperationException("No current row.");

    public override int FieldCount => _shape.Names.Length;
    public override int VisibleFieldCount => FieldCount;
    public override object this[int ordinal] => GetValue(ordinal);
    public override object this[string name] => GetValue(GetOrdinal(name));
    public override int Depth => 0;
    public override bool HasRows => true;
    public override bool IsClosed => Disposed;
    public override int RecordsAffected => -1;
    public override string GetName(int ordinal) => _shape.Names[ordinal];
    public override string GetDataTypeName(int ordinal) => _shape.TypeNames[ordinal];
    public override Type GetFieldType(int ordinal) => _shape.Types[ordinal];

    public override int GetOrdinal(string name)
    {
        for (int i = 0; i < _shape.Names.Length; i++)
            if (string.Equals(_shape.Names[i], name, StringComparison.OrdinalIgnoreCase)) return i;
        throw new IndexOutOfRangeException(name);
    }

    public override object GetValue(int ordinal) => Row[ordinal];

    public override int GetValues(object[] values)
    {
        int n = Math.Min(values.Length, FieldCount);
        Array.Copy(Row, values, n);
        return n;
    }

    public override bool IsDBNull(int ordinal) => Row[ordinal] is DBNull;
    public override bool GetBoolean(int ordinal) => (bool)Row[ordinal];
    public override byte GetByte(int ordinal) => Convert.ToByte(Row[ordinal]);
    public override char GetChar(int ordinal) => Convert.ToChar(Row[ordinal]);
    public override DateTime GetDateTime(int ordinal) => (DateTime)Row[ordinal];
    public override decimal GetDecimal(int ordinal) => Row[ordinal] is System.Data.SqlTypes.SqlDecimal d ? d.Value : Convert.ToDecimal(Row[ordinal]);
    public override double GetDouble(int ordinal) => Convert.ToDouble(Row[ordinal]);
    public override float GetFloat(int ordinal) => Convert.ToSingle(Row[ordinal]);
    public override Guid GetGuid(int ordinal) => (Guid)Row[ordinal];
    public override short GetInt16(int ordinal) => Convert.ToInt16(Row[ordinal]);
    public override int GetInt32(int ordinal) => Convert.ToInt32(Row[ordinal]);
    public override long GetInt64(int ordinal) => Convert.ToInt64(Row[ordinal]);
    public override string GetString(int ordinal) => (string)Row[ordinal];

    public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length)
    {
        var data = (byte[])Row[ordinal];
        if (buffer is null) return data.Length;
        var n = (int)Math.Max(0, Math.Min(length, data.Length - dataOffset));
        Array.Copy(data, dataOffset, buffer, bufferOffset, n);
        return n;
    }

    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length)
    {
        var data = (string)Row[ordinal];
        if (buffer is null) return data.Length;
        var n = (int)Math.Max(0, Math.Min(length, data.Length - dataOffset));
        data.CopyTo((int)dataOffset, buffer, bufferOffset, n);
        return n;
    }

    public override bool NextResult() => false;
    public override IEnumerator GetEnumerator() => new DbEnumerator(this, closeReader: false);

    protected override void Dispose(bool disposing)
    {
        Disposed = true;
        base.Dispose(disposing);
    }
}

/// <summary>
/// Reads a table with several connections at once – each one slice of the split column's range – and hands the rows to
/// ONE consumer (the bulk copy into the target, or the parallel writers), so the load stays one load: one integrity count,
/// one watermark, one roll-back. Rows arrive in no particular order. decimal values are passed as SqlDecimal (decimal(38) is exact).
/// </summary>
public sealed class ParallelSourceReader : RowBufferReader, IDbColumnSchemaGenerator
{
    private const int BatchRows = 1000;

    private readonly List<Source> _sources;
    private readonly Channel<List<object[]>> _channel;
    private readonly CancellationTokenSource _cts;
    private readonly Task[] _producers;
    private readonly ReadOnlyCollection<DbColumn> _schema;
    private readonly bool[] _isDecimal;
    private int _running;

    private sealed record Source(SqlConnection Connection, SqlCommand Command, SqlDataReader Reader);

    /// <summary>Number of readers actually used.</summary>
    public int Readers => _sources.Count;

    /// <summary>The split column and the measured range (for the log).</summary>
    public string Description { get; private set; } = "";

    private ParallelSourceReader(List<Source> sources, Channel<List<object[]>> channel, RowShape shape, CancellationToken ct)
        : base(channel.Reader, shape)
    {
        _sources = sources;
        _channel = channel;
        _schema = sources[0].Reader.GetColumnSchema();
        _isDecimal = shape.TypeNames.Select(t => t.ToLowerInvariant() is "decimal" or "numeric").ToArray();
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _running = sources.Count;
        _producers = sources.Select(s => Task.Factory.StartNew(() => Produce(s.Reader), CancellationToken.None,
            TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
    }

    private static ParallelSourceReader Create(List<Source> sources, CancellationToken ct) =>
        new(sources, Channel.CreateBounded<List<object[]>>(new BoundedChannelOptions(sources.Count * 4) { SingleReader = true, SingleWriter = false }),
            RowShape.Of(sources[0].Reader), ct);

    /// <summary>
    /// Measures MIN/MAX of the split column (with the job's filter and watermark), opens one reader per slice and starts
    /// reading. Returns null when one reader is enough (empty table, a range smaller than the reader count).
    /// </summary>
    public static async Task<ParallelSourceReader?> OpenAsync(SqlConnectionFactory factory, SqlServerDef server, ExtractJobDef job,
        ExtractState? state, bool hasWatermark, CancellationToken ct)
    {
        string column, type;
        object? min, max;
        await using (var probe = await factory.OpenAsync(server, job.SourceDatabase, forWrite: false, ct))
        {
            (column, type) = await ResolveSplitColumnAsync(probe, job, ct);
            await using var cmd = probe.CreateCommand();
            var q = SqlIdent.Quote(column);
            cmd.CommandText = SourceCommandBuilder.BuildSql(job, hasWatermark, selectList: $"MIN({q}), MAX({q})");
            cmd.CommandTimeout = job.CommandTimeoutSec;
            if (hasWatermark) cmd.Parameters.Add(Watermark.ToParameter(SourceCommandBuilder.WatermarkParam, state?.WatermarkType, state?.WatermarkValue));
            await using var r = await cmd.ExecuteReaderAsync(ct);
            await r.ReadAsync(ct);
            min = r.IsDBNull(0) ? null : r.GetValue(0);
            max = r.IsDBNull(1) ? null : r.GetValue(1);
        }

        var slices = SourceSplitter.Slices(column, type, min, max, job.ReadParallelism);
        if (slices.Count < 2) return null;

        var opening = slices.Select(async slice =>
        {
            var conn = await factory.OpenAsync(server, job.SourceDatabase, forWrite: false, ct);
            SqlCommand? cmd = null;
            try
            {
                cmd = conn.CreateCommand();
                cmd.CommandText = SourceCommandBuilder.BuildSql(job, hasWatermark, slice);
                cmd.CommandTimeout = job.CommandTimeoutSec;
                if (hasWatermark) cmd.Parameters.Add(Watermark.ToParameter(SourceCommandBuilder.WatermarkParam, state?.WatermarkType, state?.WatermarkValue));
                foreach (var p in slice.Parameters()) cmd.Parameters.Add(p);
                var reader = await cmd.ExecuteReaderAsync(ct);
                return new Source(conn, cmd, reader);
            }
            catch
            {
                if (cmd is not null) await cmd.DisposeAsync();
                await conn.DisposeAsync();
                throw;
            }
        }).ToList();

        try { await Task.WhenAll(opening); }
        catch
        {
            foreach (var t in opening.Where(t => t.IsCompletedSuccessfully)) await DisposeSourceAsync(t.Result);
            throw;
        }
        var reader = Create(opening.Select(t => t.Result).ToList(), ct);
        reader.Description = $"split on {column} ({Watermark.Serialize(min!)} – {Watermark.Serialize(max!)})";
        return reader;
    }

    /// <summary>The job's split column, or the leading key column of the clustered index (else the primary key).</summary>
    public static async Task<(string Column, string Type)> ResolveSplitColumnAsync(SqlConnection source, ExtractJobDef job, CancellationToken ct)
    {
        var table = SqlIdent.Quote(job.SourceSchema ?? "dbo", job.SourceTable!);
        await using var cmd = source.CreateCommand();
        cmd.Parameters.AddWithValue("@t", table);
        string? column = null, type = null;
        if (!string.IsNullOrWhiteSpace(job.SplitColumn))
        {
            cmd.CommandText = "SELECT c.name, TYPE_NAME(c.system_type_id) FROM sys.columns c WHERE c.object_id = OBJECT_ID(@t) AND c.name = @c;";
            cmd.Parameters.AddWithValue("@c", job.SplitColumn.Trim().Trim('[', ']'));
        }
        else
        {
            cmd.CommandText = """
                SELECT TOP (1) c.name, TYPE_NAME(c.system_type_id)
                FROM sys.indexes i
                JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.key_ordinal = 1
                JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                WHERE i.object_id = OBJECT_ID(@t) AND (i.index_id = 1 OR i.is_primary_key = 1)
                ORDER BY CASE WHEN i.index_id = 1 THEN 0 ELSE 1 END;
                """;
        }
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            if (await r.ReadAsync(ct)) { column = r.GetString(0); type = r.GetString(1); }

        if (column is null)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(job.SplitColumn)
                ? $"Parallel reads: {table} has no clustered index or primary key to split on; choose a split column (an indexed integer or date column)."
                : $"Parallel reads: the split column '{job.SplitColumn}' is not a column of {table}.");
        if (!SourceSplitter.SplitTypes.Contains(type!))
            throw new InvalidOperationException(
                $"Parallel reads: the split column {column} is {type}; use an integer or date/time column (ideally the leading column of an index).");
        return (column, type!.ToLowerInvariant());
    }

    private void Produce(SqlDataReader r)
    {
        try
        {
            int n = FieldCount;
            var batch = new List<object[]>(BatchRows);
            while (r.Read())
            {
                var row = new object[n];
                // per column: GetValue of a decimal(38) value beyond .NET decimal would overflow
                for (int i = 0; i < n; i++)
                    row[i] = _isDecimal[i] ? (r.IsDBNull(i) ? DBNull.Value : r.GetSqlDecimal(i)) : r.GetValue(i);
                batch.Add(row);
                if (batch.Count == BatchRows)
                {
                    _channel.Writer.WriteAsync(batch, _cts.Token).AsTask().GetAwaiter().GetResult();
                    batch = new List<object[]>(BatchRows);
                }
            }
            if (batch.Count > 0) _channel.Writer.WriteAsync(batch, _cts.Token).AsTask().GetAwaiter().GetResult();
            if (Interlocked.Decrement(ref _running) == 0) _channel.Writer.TryComplete();
        }
        catch (Exception ex)
        {
            // the first failure completes the channel (the consumer rethrows it), then the other readers stop
            _channel.Writer.TryComplete(ex);
            try { _cts.Cancel(); } catch (ObjectDisposedException) { }
        }
    }

    public ReadOnlyCollection<DbColumn> GetColumnSchema() => _schema;

    protected override void Dispose(bool disposing)
    {
        if (disposing && !Disposed)
        {
            Disposed = true;
            try { _cts.Cancel(); } catch (ObjectDisposedException) { }
            foreach (var s in _sources) try { s.Command.Cancel(); } catch { /* the reader may already be finished */ }
            _channel.Writer.TryComplete();
            try { Task.WaitAll(_producers, TimeSpan.FromSeconds(30)); } catch { /* failures were reported through the channel */ }
            foreach (var s in _sources)
            {
                try { s.Reader.Dispose(); } catch { }
                try { s.Command.Dispose(); } catch { }
                try { s.Connection.Dispose(); } catch { }
            }
            _cts.Dispose();
        }
        base.Dispose(disposing);
    }

    private static async Task DisposeSourceAsync(Source s)
    {
        await s.Reader.DisposeAsync();
        await s.Command.DisposeAsync();
        await s.Connection.DisposeAsync();
    }
}
