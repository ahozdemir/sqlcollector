using System.Data.Common;
using System.Threading.Channels;
using Microsoft.Data.SqlClient;

namespace SqlDataCollector.Extract;

/// <summary>
/// Parallel writes: the rows of ONE reader (with its technical columns, masks and watermark tracking) are dealt out in
/// batches to N bulk copies, each on its own target connection – N streams compress columnstore rowgroups and send rows
/// at the same time. The load stays one load: the rows carry the same _LoadId, the integrity count and the clean-up of a
/// failed run work by _LoadId as before. Not for a Merge (its staging table belongs to one session).
/// </summary>
public static class ParallelBulkWriter
{
    /// <summary>Rows per batch handed to a writer (round robin).</summary>
    public const int BatchRows = 1000;

    /// <param name="source">The reader (read on the calling thread).</param>
    /// <param name="openTarget">Opens one target connection per writer.</param>
    /// <param name="configure">Sets destination table, batch size and column mappings of each writer's bulk copy (argument: writer number).</param>
    /// <returns>Rows read from <paramref name="source"/> and handed to the writers.</returns>
    public static async Task<long> WriteAsync(DbDataReader source, int writers, Func<CancellationToken, Task<SqlConnection>> openTarget,
        SqlBulkCopyOptions options, Action<SqlBulkCopy, int> configure, CancellationToken ct)
    {
        if (writers < 2) throw new ArgumentOutOfRangeException(nameof(writers), "Parallel writes need at least two writers.");
        var shape = RowShape.Of(source);
        var channels = Enumerable.Range(0, writers)
            .Select(_ => Channel.CreateBounded<List<object[]>>(new BoundedChannelOptions(4) { SingleReader = true, SingleWriter = true }))
            .ToArray();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Exception? failure = null;
        void Fail(Exception ex)
        {
            Interlocked.CompareExchange(ref failure, ex, null);
            try { cts.Cancel(); } catch (ObjectDisposedException) { }
        }

        var tasks = channels.Select((channel, i) => Task.Run(async () =>
        {
            try
            {
                await using var conn = await openTarget(cts.Token);
                using var bulk = new SqlBulkCopy(conn, options, null);
                configure(bulk, i);
                using var rows = new RowBufferReader(channel.Reader, shape);
                await bulk.WriteToServerAsync(rows, cts.Token);
            }
            catch (Exception ex)
            {
                Fail(ex);
                channel.Writer.TryComplete();   // the dealer stops handing rows to this writer
            }
        })).ToArray();

        long read = 0;
        try
        {
            int n = shape.Names.Length, next = 0;
            var batch = new List<object[]>(BatchRows);
            while (source.Read())
            {
                var row = new object[n];
                source.GetValues(row);
                batch.Add(row);
                read++;
                if (batch.Count == BatchRows)
                {
                    await channels[next].Writer.WriteAsync(batch, cts.Token);
                    next = (next + 1) % writers;
                    batch = new List<object[]>(BatchRows);
                }
            }
            if (batch.Count > 0) await channels[next].Writer.WriteAsync(batch, cts.Token);
            foreach (var c in channels) c.Writer.TryComplete();
        }
        catch (Exception ex)
        {
            // a writer failed (the write was cancelled) or the source failed: stop everything, report the first real error
            if (ex is not OperationCanceledException || failure is null) Fail(ex);
            foreach (var c in channels) c.Writer.TryComplete(failure);
        }

        await Task.WhenAll(tasks);
        if (failure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(failure);
        return read;
    }
}
