using System.Globalization;
using Microsoft.Data.SqlClient;
using SqlDataCollector.Infrastructure;
using SqlDataCollector.Models;

namespace SqlDataCollector.Extract;

/// <summary>
/// What a Change Tracking run reads: the changes since <see cref="FromVersion"/> (null = the whole table), and the version
/// stored after success. Keys = the source table's primary key columns.
/// </summary>
public sealed record ChangeTrackingRead(long CurrentVersion, long? FromVersion, IReadOnlyList<string> Keys, string Reason)
{
    public bool IsDelta => FromVersion is not null;
}

/// <summary>SQL of the Change Tracking source (pure string building; unit tested).</summary>
public static class ChangeTrackingSql
{
    /// <summary>etl.ExtractState.WatermarkType of a stored change tracking version.</summary>
    public const string VersionType = "ctversion";
    public const string FromParam = "@CtFrom";

    /// <summary>Current database version, the table's oldest valid version, then the primary key columns in order.</summary>
    public const string Probe = """
        SELECT CHANGE_TRACKING_CURRENT_VERSION(), CHANGE_TRACKING_MIN_VALID_VERSION(OBJECT_ID(@t));
        SELECT c.name
        FROM sys.indexes i
        JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
        JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
        WHERE i.object_id = OBJECT_ID(@t) AND i.is_primary_key = 1
        ORDER BY ic.key_ordinal;
        """;

    private static string Table(ExtractJobDef job) => SqlIdent.Quote(job.SourceSchema ?? "dbo", job.SourceTable!);
    private static string NoLock(ExtractJobDef job) => job.UseNoLock && !job.ReadSnapshot ? " WITH (NOLOCK)" : "";
    private static string Join(IReadOnlyList<string> keys) => string.Join(" AND ", keys.Select(k => $"ct.{SqlIdent.Quote(k)} = s.{SqlIdent.Quote(k)}"));

    /// <summary>
    /// The current values of the rows whose key changed since @CtFrom and that pass the job's filter (upserted).
    /// The job's column list and filter refer to the table (alias s) unqualified, exactly as in a full read.
    /// </summary>
    public static string ChangedRows(ExtractJobDef job, IReadOnlyList<string> keys)
    {
        var table = Table(job);
        var cols = string.IsNullOrWhiteSpace(job.ColumnList) ? "*" : job.ColumnList;
        var where = new List<string>();
        if (!string.IsNullOrWhiteSpace(job.FilterClause)) where.Add($"({job.FilterClause})");
        where.Add($"EXISTS (SELECT 1 FROM CHANGETABLE(CHANGES {table}, {FromParam}) AS ct WHERE {Join(keys)})");
        return $"SELECT {cols} FROM {table} AS s{NoLock(job)} WHERE {string.Join(" AND ", where)}";
    }

    /// <summary>
    /// Keys that changed since @CtFrom but are no longer in the table – or no longer pass the job's filter (deleted from the target).
    /// </summary>
    public static string DeletedKeys(ExtractJobDef job, IReadOnlyList<string> keys)
    {
        var table = Table(job);
        var filter = string.IsNullOrWhiteSpace(job.FilterClause) ? "" : $" AND ({job.FilterClause})";
        return $"SELECT {string.Join(", ", keys.Select(k => $"ct.{SqlIdent.Quote(k)}"))} FROM CHANGETABLE(CHANGES {table}, {FromParam}) AS ct " +
               $"WHERE NOT EXISTS (SELECT 1 FROM {table} AS s{NoLock(job)} WHERE {Join(keys)}{filter})";
    }

    /// <summary>Decides between reading the changes since the stored version and a full read (pure; unit tested).</summary>
    public static ChangeTrackingRead Decide(long current, long minValid, long? stored, IReadOnlyList<string> keys) => stored switch
    {
        null => new(current, null, keys, "first run: full read"),
        { } s when s < minValid => new(current, null, keys,
            $"the stored version {s} is older than the change retention (oldest valid {minValid}): full read"),
        { } s when s > current => new(current, null, keys,
            $"the stored version {s} is newer than the database's current version {current} (database restored?): full read"),
        { } s => new(current, s, keys, $"changes since version {s} (current {current})")
    };

    /// <summary>The stored version of a job, or null when there is none (or the state is a watermark of another mode).</summary>
    public static long? StoredVersion(ExtractState? state) =>
        state is { WatermarkType: VersionType, WatermarkValue: { } v } && long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var x) ? x : null;
}

/// <summary>Reads the change tracking position of a source table (version, retention, primary key).</summary>
public static class ChangeTrackingSource
{
    /// <summary>
    /// Run on the source connection (inside the snapshot transaction when the job reads a consistent snapshot) BEFORE the
    /// changes are read: a change made while reading has a higher version and is simply read again by the next run.
    /// </summary>
    public static async Task<ChangeTrackingRead> PrepareAsync(SqlConnection source, SqlTransaction? tx, ExtractJobDef job, ExtractState? state, CancellationToken ct)
    {
        var table = SqlIdent.Quote(job.SourceSchema ?? "dbo", job.SourceTable!);
        long? current, minValid;
        var keys = new List<string>();
        await using (var cmd = source.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = ChangeTrackingSql.Probe;
            cmd.CommandTimeout = job.CommandTimeoutSec;
            cmd.Parameters.AddWithValue("@t", table);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            await r.ReadAsync(ct);
            current = r.IsDBNull(0) ? null : r.GetInt64(0);
            minValid = r.IsDBNull(1) ? null : r.GetInt64(1);
            await r.NextResultAsync(ct);
            while (await r.ReadAsync(ct)) keys.Add(r.GetString(0));
        }

        if (current is null)
            throw new InvalidOperationException(
                $"Change tracking is not enabled in source database {job.SourceDatabase}. The DBA enables it once with " +
                $"ALTER DATABASE {SqlIdent.Quote(job.SourceDatabase)} SET CHANGE_TRACKING = ON (CHANGE_RETENTION = 7 DAYS, AUTO_CLEANUP = ON); – " +
                "then ALTER TABLE … ENABLE CHANGE_TRACKING; for each table.");
        if (minValid is null)
            throw new InvalidOperationException(
                $"Change tracking is not enabled for table {table}, or the service account may not see it. The DBA runs " +
                $"ALTER TABLE {table} ENABLE CHANGE_TRACKING; and GRANT VIEW CHANGE TRACKING ON {table} TO <service account>;");
        if (keys.Count == 0)
            throw new InvalidOperationException($"Change tracking needs a primary key on {table}.");

        var jobKeys = job.KeyColumnList();
        if (jobKeys.Count > 0 && !(jobKeys.Count == keys.Count && jobKeys.All(k => keys.Contains(k, StringComparer.OrdinalIgnoreCase))))
            throw new InvalidOperationException(
                $"Change tracking identifies rows by the primary key ({string.Join(", ", keys)}); set the key columns to it (now: {string.Join(", ", jobKeys)}).");

        return ChangeTrackingSql.Decide(current.Value, minValid.Value, ChangeTrackingSql.StoredVersion(state), keys);
    }
}
