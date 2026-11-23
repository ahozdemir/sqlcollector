using System.Text;
using Microsoft.Data.SqlClient;
using SqlDataCollector.Infrastructure;
using SqlDataCollector.Models;

namespace SqlDataCollector.Extract;

/// <summary>
/// What a merge writes: target table, key columns, the other source columns (target names) and
/// whether rows missing from the source are deleted. Columns not delivered by the source are never touched.
/// DeleteKeys: Change Tracking – the keys staged in #sdc_delete (rows deleted in the source) are deleted first.
/// </summary>
public sealed record MergePlan(
    string Schema, string Table,
    IReadOnlyList<string> Keys,
    IReadOnlyList<string> Columns,
    IReadOnlySet<string> XmlColumns,
    bool DeleteMissing,
    int ExtractJobId,
    bool DeleteKeys = false);

/// <summary>SQL of the Merge load mode (pure string building; unit tested).</summary>
public static class MergeSql
{
    public const string Staging = "#sdc_merge";
    public const string DeleteStaging = "#sdc_delete";

    /// <summary>Empty session temp table with the target's key columns: the keys Change Tracking reports as deleted.</summary>
    public static string CreateDeleteStaging(string schema, string table, IReadOnlyList<string> keys)
    {
        var full = SqlIdent.Quote(schema, table);
        var cols = string.Join(", ", keys.Select(SqlIdent.Quote));
        return $"""
            IF OBJECT_ID(N'tempdb..{DeleteStaging}') IS NOT NULL DROP TABLE {DeleteStaging};
            SELECT TOP (0) {cols} INTO {DeleteStaging} FROM {full} UNION ALL SELECT TOP (0) {cols} FROM {full};
            """;
    }

    /// <summary>Empty session temp table with the target's columns (UNION ALL drops an IDENTITY property).</summary>
    public static string CreateStaging(string schema, string table)
    {
        var full = SqlIdent.Quote(schema, table);
        return $"""
            IF OBJECT_ID(N'tempdb..{Staging}') IS NOT NULL DROP TABLE {Staging};
            SELECT TOP (0) * INTO {Staging} FROM {full} UNION ALL SELECT TOP (0) * FROM {full};
            """;
    }

    /// <summary>Index on the staged keys (join speed) and the checks: NULL keys, duplicate keys (sample of 5).</summary>
    public static string IndexAndValidate(MergePlan p)
    {
        var keys = string.Join(", ", p.Keys.Select(SqlIdent.Quote));
        var anyNull = string.Join(" OR ", p.Keys.Select(k => $"{SqlIdent.Quote(k)} IS NULL"));
        var shown = string.Join(", N', ', ", p.Keys.Select(k => $"CONVERT(nvarchar(200), {SqlIdent.Quote(k)})"));
        return $"""
            CREATE CLUSTERED INDEX CX_sdc_merge ON {Staging} ({keys});
            SELECT COUNT_BIG(*) FROM {Staging} WHERE {anyNull};
            SELECT TOP (5) CONCAT(N'(', {shown}, N')') AS KeyValue, COUNT_BIG(*) AS Copies
            FROM {Staging} GROUP BY {keys} HAVING COUNT_BIG(*) > 1 ORDER BY COUNT_BIG(*) DESC;
            """;
    }

    /// <summary>
    /// UPDATE rows whose values changed (EXCEPT compares NULLs as equal), INSERT new keys, optionally DELETE keys
    /// of this job that the source no longer returns. Returns one row: inserted, updated, deleted.
    /// </summary>
    public static string Merge(MergePlan p, bool delete)
    {
        var t = SqlIdent.Quote(p.Schema, p.Table);
        string Q(string c) => SqlIdent.Quote(c);
        string Cmp(string alias, string c) => p.XmlColumns.Contains(c) ? $"CONVERT(nvarchar(max), {alias}.{Q(c)})" : $"{alias}.{Q(c)}";
        var join = string.Join(" AND ", p.Keys.Select(k => $"t.{Q(k)} = s.{Q(k)}"));
        var tech = SqlIdent.TechnicalColumns;

        var sb = new StringBuilder("SET XACT_ABORT ON;\nDECLARE @ins bigint = 0, @upd bigint = 0, @del bigint = 0;\n");
        if (p.DeleteKeys)
        {
            // first, so a key deleted and inserted again between the two source reads ends up present (the next run settles it anyway)
            var keyJoin = string.Join(" AND ", p.Keys.Select(k => $"t.{Q(k)} = d.{Q(k)}"));
            sb.AppendLine($"CREATE CLUSTERED INDEX CX_sdc_delete ON {DeleteStaging} ({string.Join(", ", p.Keys.Select(Q))});");
            sb.AppendLine($"DELETE t FROM {t} AS t");
            sb.AppendLine($"WHERE t.{Q(SqlIdent.ExtractJobIdColumn)} = @job AND EXISTS (SELECT 1 FROM {DeleteStaging} AS d WHERE {keyJoin});");
            sb.AppendLine("SET @del = @@ROWCOUNT;");
        }
        if (p.Columns.Count > 0)
        {
            var set = string.Join(", ", p.Columns.Concat(tech).Select(c => $"{Q(c)} = s.{Q(c)}"));
            sb.AppendLine($"UPDATE t SET {set}");
            sb.AppendLine($"FROM {t} AS t INNER JOIN {Staging} AS s ON {join}");
            sb.AppendLine($"WHERE EXISTS (SELECT {string.Join(", ", p.Columns.Select(c => Cmp("s", c)))}");
            sb.AppendLine($"              EXCEPT SELECT {string.Join(", ", p.Columns.Select(c => Cmp("t", c)))});");
            sb.AppendLine("SET @upd = @@ROWCOUNT;");
        }

        var insertCols = string.Join(", ", p.Keys.Concat(p.Columns).Concat(tech).Select(Q));
        sb.AppendLine($"INSERT {t} ({insertCols})");
        sb.AppendLine($"SELECT {string.Join(", ", p.Keys.Concat(p.Columns).Concat(tech).Select(c => $"s.{Q(c)}"))} FROM {Staging} AS s");
        sb.AppendLine($"WHERE NOT EXISTS (SELECT 1 FROM {t} AS t WHERE {join});");
        sb.AppendLine("SET @ins = @@ROWCOUNT;");

        if (delete)
        {
            sb.AppendLine($"DELETE t FROM {t} AS t");
            sb.AppendLine($"WHERE t.{Q(SqlIdent.ExtractJobIdColumn)} = @job AND NOT EXISTS (SELECT 1 FROM {Staging} AS s WHERE {join});");
            sb.AppendLine("SET @del = @del + @@ROWCOUNT;");
        }
        sb.AppendLine($"DROP TABLE {Staging};");
        if (p.DeleteKeys) sb.AppendLine($"DROP TABLE {DeleteStaging};");
        sb.Append("SELECT @ins, @upd, @del;");
        return sb.ToString();
    }
}

/// <summary>
/// Merge load mode: the source rows are bulk copied into a session temp table, checked (no NULL or duplicate
/// keys), then applied to the target in ONE transaction. A failure rolls back everything; nothing to clean up.
/// </summary>
public sealed class MergeLoader(ILogger<MergeLoader> logger)
{
    public async Task CreateStagingAsync(SqlConnection conn, string schema, string table, CancellationToken ct) =>
        await ExecAsync(conn, null, MergeSql.CreateStaging(schema, table), ct);

    public async Task CreateDeleteStagingAsync(SqlConnection conn, string schema, string table, IReadOnlyList<string> keys, CancellationToken ct) =>
        await ExecAsync(conn, null, MergeSql.CreateDeleteStaging(schema, table, keys), ct);

    public async Task<IReadOnlySet<string>> GetXmlColumnsAsync(SqlConnection conn, string schema, string table, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sys.columns WHERE object_id = OBJECT_ID(@t, N'U') AND system_type_id = 241;";
        cmd.Parameters.AddWithValue("@t", SqlIdent.Quote(schema, table));
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) set.Add(r.GetString(0));
        return set;
    }

    public async Task<MergeCounts> MergeAsync(SqlConnection conn, MergePlan plan, long rowsRead, string jobName, CancellationToken ct)
    {
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = MergeSql.IndexAndValidate(plan);
            cmd.CommandTimeout = 0;
            await using var r = await cmd.ExecuteReaderAsync(ct);
            await r.ReadAsync(ct);
            var nullKeys = r.GetInt64(0);
            if (nullKeys > 0)
                throw new InvalidOperationException(
                    $"Merge: {nullKeys:N0} source row(s) have an empty (NULL) key ({string.Join(", ", plan.Keys)}); filter them out or choose other key columns.");
            await r.NextResultAsync(ct);
            var dups = new List<string>();
            while (await r.ReadAsync(ct)) dups.Add($"{r.GetString(0)} ×{r.GetInt64(1)}");
            if (dups.Count > 0)
                throw new InvalidOperationException(
                    $"Merge: the source returns the same key more than once ({string.Join(", ", plan.Keys)}): {string.Join("; ", dups)}. " +
                    "The key must identify one row; add columns to the key or remove the duplicates in the query.");
        }

        // a source that suddenly returns nothing must not empty the table
        var delete = plan.DeleteMissing && rowsRead > 0;
        if (plan.DeleteMissing && !delete)
            logger.LogWarning("Merge {Name}: the source returned no rows; deleting missing rows was skipped.", jobName);

        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        await using var mc = conn.CreateCommand();
        mc.Transaction = tx;
        mc.CommandText = MergeSql.Merge(plan, delete);
        mc.CommandTimeout = 0;
        mc.Parameters.AddWithValue("@job", plan.ExtractJobId);
        MergeCounts counts;
        await using (var r = await mc.ExecuteReaderAsync(ct))
        {
            await r.ReadAsync(ct);
            counts = new MergeCounts(r.GetInt64(0), r.GetInt64(1), r.GetInt64(2));
        }
        await tx.CommitAsync(ct);
        return counts;
    }

    private static async Task ExecAsync(SqlConnection conn, SqlTransaction? tx, string sql, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.CommandTimeout = 0;
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
