using System.Globalization;
using Microsoft.Data.SqlClient;
using SqlDataCollector.Infrastructure;
using SqlDataCollector.Models;

namespace SqlDataCollector.Quality;

/// <summary>Pure parts of the data quality checks (comparisons, deviation, SQL); unit tested.</summary>
public static class QualityRules
{
    public const int RowChangeHistory = 10;
    public const int RowChangeMinHistory = 3;
    public static readonly string[] Operators = ["=", "<>", "<", "<=", ">", ">="];

    public static bool Compare(decimal value, string? op, decimal threshold) => op switch
    {
        "=" => value == threshold,
        "<>" => value != threshold,
        "<" => value < threshold,
        "<=" => value <= threshold,
        ">" => value > threshold,
        ">=" => value >= threshold,
        _ => throw new InvalidOperationException($"Unknown operator '{op}'.")
    };

    /// <summary>Failed &gt; Warning &gt; Passed; null when there is nothing.</summary>
    public static string? Worst(IEnumerable<string> statuses)
    {
        var list = statuses.ToList();
        if (list.Count == 0) return null;
        if (list.Contains(QualityStatus.Failed)) return QualityStatus.Failed;
        return list.Contains(QualityStatus.Warning) ? QualityStatus.Warning : QualityStatus.Passed;
    }

    /// <summary>Deviation in % of rows from the average of earlier loads; null when there is too little history or the average is 0.</summary>
    public static (decimal Percent, decimal Average)? Deviation(long rows, IReadOnlyList<long> history)
    {
        if (history.Count < RowChangeMinHistory) return null;
        var avg = (decimal)history.Average();
        if (avg == 0) return null;
        return (Math.Round(Math.Abs(rows - avg) / avg * 100, 2), Math.Round(avg, 0));
    }

    /// <summary>Rows checked by a NullRate/Duplicates check: this load or all rows of the job.</summary>
    public static string ScopeFilter(string scope) =>
        scope == "Table" ? $"{SqlIdent.Quote(SqlIdent.ExtractJobIdColumn)} = @job" : $"{SqlIdent.Quote(SqlIdent.LoadIdColumn)} = @load";

    public static string NullRateSql(string schema, string table, string column, string scope) => $"""
        SELECT COUNT_BIG(*), SUM(CASE WHEN {SqlIdent.Quote(column)} IS NULL THEN CAST(1 AS bigint) ELSE 0 END)
        FROM {SqlIdent.Quote(schema, table)} WHERE {ScopeFilter(scope)};
        """;

    public static IReadOnlyList<string> Columns(string? list) =>
        (list ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(c => c.Trim('[', ']')).Where(c => c.Length > 0).ToList();

    public static string DuplicatesSql(string schema, string table, IReadOnlyList<string> columns, string scope)
    {
        var cols = string.Join(", ", columns.Select(SqlIdent.Quote));
        return $"""
            SELECT COUNT_BIG(*) FROM (
                SELECT {cols} FROM {SqlIdent.Quote(schema, table)} WHERE {ScopeFilter(scope)}
                GROUP BY {cols} HAVING COUNT_BIG(*) > 1) d;
            """;
    }

    /// <summary>
    /// Source row count of a table source with the job's own filter and NOLOCK setting. With a cutoff column (an increasing ID or an
    /// insert time) only rows up to the highest value that was loaded are counted, so rows added after the read do not count.
    /// </summary>
    public static string SourceCountSql(ExtractJobDef job, string? cutoffColumn = null)
    {
        var sql = $"SELECT COUNT_BIG(*) FROM {SqlIdent.Quote(job.SourceSchema ?? "dbo", job.SourceTable!)}{(job.UseNoLock && !job.ReadSnapshot ? " WITH (NOLOCK)" : "")}";
        var predicates = new List<string>();
        if (!string.IsNullOrWhiteSpace(job.FilterClause)) predicates.Add($"({job.FilterClause})");
        if (!string.IsNullOrWhiteSpace(cutoffColumn)) predicates.Add($"{SqlIdent.Quote(cutoffColumn)} <= @cutoff");
        return predicates.Count == 0 ? sql : $"{sql} WHERE {string.Join(" AND ", predicates)}";
    }

    /// <summary>Why a check cannot be used for a job, or null (checked on save and at run time).</summary>
    public static string? Validate(QualityCheckDef q, ExtractJobDef job)
    {
        switch (q.CheckType)
        {
            case QualityCheckTypes.SourceCount:
                if (job.SourceType != SourceType.Table) return "Source count needs a table or view source (a query cannot be counted reliably).";
                if (job.LoadMode == LoadMode.Incremental) return "Source count does not apply to Incremental jobs (the target holds every changed version of a row).";
                break;
            case QualityCheckTypes.NullRate:
                if (string.IsNullOrWhiteSpace(q.ColumnName)) return "Choose the column whose NULL share is checked.";
                break;
            case QualityCheckTypes.Duplicates:
                if (Columns(q.ColumnName).Count == 0) return "Choose the column(s) that must be unique.";
                break;
            case QualityCheckTypes.CustomSql:
                if (string.IsNullOrWhiteSpace(q.SqlText)) return "Enter the SQL that returns one number.";
                if (!Operators.Contains(q.Operator)) return "Choose a comparison operator.";
                if (q.Threshold is null) return "Enter the value to compare with.";
                break;
            case QualityCheckTypes.MinRows or QualityCheckTypes.RowChange:
                break;
            default:
                return $"Unknown check type '{q.CheckType}'.";
        }
        if (q.Threshold is < 0) return "The threshold cannot be negative.";
        return null;
    }

    /// <summary>Short description of a check for lists and alerts.</summary>
    public static string Describe(QualityCheckDef q) => q.CheckType switch
    {
        QualityCheckTypes.MinRows => $"At least {q.Threshold ?? 1:0.##} row(s) per load",
        QualityCheckTypes.RowChange => $"Rows within ±{q.Threshold ?? 50:0.##} % of the recent average",
        QualityCheckTypes.SourceCount => $"Row count matches the source (tolerance {q.Threshold ?? 0:0.##} %{(string.IsNullOrWhiteSpace(q.ColumnName) ? "" : ", cutoff " + q.ColumnName)})",
        QualityCheckTypes.NullRate => $"{q.ColumnName}: at most {q.Threshold ?? 0:0.##} % NULL ({ScopeText(q.Scope)})",
        QualityCheckTypes.Duplicates => $"{q.ColumnName}: at most {q.Threshold ?? 0:0.##} duplicate key(s) ({ScopeText(q.Scope)})",
        QualityCheckTypes.CustomSql => $"Custom SQL {q.Operator} {q.Threshold:0.####}",
        _ => q.CheckType
    };

    private static string ScopeText(string scope) => scope == "Table" ? "all rows" : "this load";
}

public static class QualityStatus
{
    public const string Passed = "Passed", Warning = "Warning", Failed = "Failed", Error = "Error";
}

/// <summary>
/// Runs a job's active quality checks right after a successful load (in the Worker that loaded it) and stores the
/// results; the load's QualityStatus is the worst outcome. Never throws: the load itself already succeeded.
/// Custom SQL runs as the read-only export reader (EXECUTE AS), like export queries.
/// </summary>
public sealed class QualityService(MetadataRepository repo, SqlConnectionFactory factory, ILogger<QualityService> logger)
{
    public async Task<string?> RunAsync(ExtractJobDef job, long loadId, long rowsRead, SqlServerDef source, SqlServerDef target,
        SqlConnection targetConn, CancellationToken ct)
    {
        try
        {
            var checks = (await repo.GetQualityChecksAsync(job.ExtractJobId)).Where(c => c.IsActive).ToList();
            if (checks.Count == 0) return null;

            var results = new List<QualityResultRow>();
            foreach (var q in checks)
            {
                var r = new QualityResultRow { QualityCheckId = q.QualityCheckId, ExtractJobId = job.ExtractJobId, LoadRunId = loadId };
                try
                {
                    if (QualityRules.Validate(q, job) is { } invalid) throw new InvalidOperationException(invalid);
                    var (ok, measured, expected, message) = await EvaluateAsync(q, job, loadId, rowsRead, source, target, targetConn, ct);
                    r.Status = ok ? QualityStatus.Passed : q.Severity == "Warning" ? QualityStatus.Warning : QualityStatus.Failed;
                    r.Measured = measured; r.Expected = expected; r.Message = message;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    r.Status = QualityStatus.Error;
                    r.Message = Truncate($"The check could not run: {ex.Message}");
                }
                results.Add(r);
            }

            // a check that could not run counts as failed (Error severity) or as a warning
            var overall = QualityRules.Worst(results.Select((r, i) => r.Status == QualityStatus.Error
                ? (checks[i].Severity == "Warning" ? QualityStatus.Warning : QualityStatus.Failed) : r.Status));
            await repo.SaveQualityResultsAsync(loadId, overall, results);

            var problems = results.Where(r => r.Status != QualityStatus.Passed).ToList();
            if (problems.Count == 0)
                logger.LogInformation("Quality {Name}: {Count} check(s) passed (LoadId={LoadId}).", job.Name, results.Count, loadId);
            else
                logger.LogWarning("Quality {Name}: {Status} – {Problems}", job.Name, overall, string.Join("; ", problems.Select(p => p.Message)));
            return overall;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Quality checks of {Name} could not be recorded (LoadId={LoadId}).", job.Name, loadId);
            return null;
        }
    }

    private async Task<(bool Ok, decimal? Measured, string Expected, string Message)> EvaluateAsync(QualityCheckDef q, ExtractJobDef job,
        long loadId, long rowsRead, SqlServerDef source, SqlServerDef target, SqlConnection targetConn, CancellationToken ct)
    {
        var inv = CultureInfo.InvariantCulture;
        switch (q.CheckType)
        {
            case QualityCheckTypes.MinRows:
            {
                var min = q.Threshold ?? 1;
                return (rowsRead >= min, rowsRead, $">= {min:0.##}", $"{rowsRead:N0} row(s) loaded, at least {min:0.##} expected.");
            }
            case QualityCheckTypes.RowChange:
            {
                var max = q.Threshold ?? 50;
                var history = await repo.GetRecentLoadedRowsAsync(job.ExtractJobId, loadId, QualityRules.RowChangeHistory);
                if (QualityRules.Deviation(rowsRead, history) is not { } d)
                    return (true, null, $"<= {max:0.##} %", $"Not enough history yet ({history.Count} of {QualityRules.RowChangeMinHistory} earlier loads, or average 0).");
                return (d.Percent <= max, d.Percent, $"<= {max:0.##} %",
                    $"{rowsRead:N0} row(s) loaded; the average of the last {history.Count} loads is {d.Average:N0} ({d.Percent:0.##} % deviation, at most {max:0.##} %).");
            }
            case QualityCheckTypes.SourceCount:
            {
                var scope = job.LoadMode == LoadMode.Merge ? "Table" : "Load";
                var targetTable = SqlIdent.Quote(job.TargetSchema, job.TargetTable);
                var tgt = await ScalarAsync(targetConn, $"SELECT COUNT_BIG(*) FROM {targetTable} WHERE {QualityRules.ScopeFilter(scope)}", job, loadId, ct) ?? 0;

                // cutoff: the highest value of an increasing column (ID, insert time) that was loaded; later source rows do not count
                var cutoffColumn = string.IsNullOrWhiteSpace(q.ColumnName) ? null : q.ColumnName.Trim().Trim('[', ']');
                object? cutoff = null;
                if (cutoffColumn is not null)
                {
                    await using var mc = Command(targetConn, $"SELECT MAX({SqlIdent.Quote(cutoffColumn)}) FROM {targetTable} WHERE {QualityRules.ScopeFilter(scope)}", job, loadId);
                    var max = await mc.ExecuteScalarAsync(ct);
                    cutoff = max is DBNull ? null : max;
                }

                long src;
                await using (var sc = await factory.OpenAsync(source, job.SourceDatabase, forWrite: false, ct))
                {
                    // a job that reads a snapshot also counts in one
                    await using var tx = job.ReadSnapshot ? (SqlTransaction)await sc.BeginTransactionAsync(System.Data.IsolationLevel.Snapshot, ct) : null;
                    await using var cmd = Command(sc, QualityRules.SourceCountSql(job, cutoff is null ? null : cutoffColumn), job, loadId);
                    cmd.Transaction = tx;
                    if (cutoff is not null) cmd.Parameters.AddWithValue("@cutoff", cutoff);
                    var v = await cmd.ExecuteScalarAsync(ct);
                    src = v is null or DBNull ? 0 : Convert.ToInt64(v, inv);
                }
                var tolerance = q.Threshold ?? 0;
                var diff = src == 0 ? (tgt == 0 ? 0 : 100) : Math.Round(Math.Abs(src - tgt) * 100m / src, 4);
                var upTo = cutoff is null ? "" : $" with {cutoffColumn} <= {(cutoff is byte[] b ? "0x" + Convert.ToHexString(b) : Privacy.Masker.ToText(cutoff))}";
                return (diff <= tolerance, tgt, $"{src:N0} ± {tolerance:0.##} %",
                    $"Source {src:N0} row(s){upTo}, target {tgt:N0} row(s){(job.LoadMode == LoadMode.Merge ? " (current state)" : " (this load)")}; difference {diff:0.##} %, at most {tolerance:0.##} %.");
            }
            case QualityCheckTypes.NullRate:
            {
                var column = q.ColumnName!.Trim().Trim('[', ']');
                await using var cmd = Command(targetConn, QualityRules.NullRateSql(job.TargetSchema, job.TargetTable, column, q.Scope), job, loadId);
                await using var rd = await cmd.ExecuteReaderAsync(ct);
                await rd.ReadAsync(ct);
                long total = rd.GetInt64(0), nulls = rd.IsDBNull(1) ? 0 : rd.GetInt64(1);
                var pct = total == 0 ? 0 : Math.Round(nulls * 100m / total, 2);
                var max = q.Threshold ?? 0;
                return (pct <= max, pct, $"<= {max:0.##} %", $"{column}: {nulls:N0} of {total:N0} row(s) NULL ({pct:0.##} %, at most {max:0.##} %).");
            }
            case QualityCheckTypes.Duplicates:
            {
                var cols = QualityRules.Columns(q.ColumnName);
                var dup = await ScalarAsync(targetConn, QualityRules.DuplicatesSql(job.TargetSchema, job.TargetTable, cols, q.Scope), job, loadId, ct) ?? 0;
                var max = q.Threshold ?? 0;
                return (dup <= max, dup, $"<= {max:0.##}", $"{string.Join(", ", cols)}: {dup:N0} key value(s) occur more than once (at most {max:0.##}).");
            }
            case QualityCheckTypes.CustomSql:
            {
                // free SQL of an Editor: run as the read-only export reader, never with the Worker's write rights
                await using var session = await factory.OpenExportReadAsync(target, job.TargetDatabase, forWrite: true, ct);
                var sql = q.SqlText!.Replace("{Table}", SqlIdent.Quote(job.TargetSchema, job.TargetTable), StringComparison.OrdinalIgnoreCase);
                await using var cmd = Command(session.Connection, sql, job, loadId);
                cmd.Parameters.AddWithValue("@LoadId", loadId);
                cmd.Parameters.AddWithValue("@ExtractJobId", job.ExtractJobId);
                var raw = await cmd.ExecuteScalarAsync(ct);
                if (raw is null or DBNull) throw new InvalidOperationException("The SQL returned no value.");
                var value = Convert.ToDecimal(raw, inv);
                var ok = QualityRules.Compare(value, q.Operator, q.Threshold!.Value);
                return (ok, value, $"{q.Operator} {q.Threshold:0.####}", $"The SQL returned {value.ToString("0.####", inv)}; expected {q.Operator} {q.Threshold:0.####}.");
            }
            default:
                throw new InvalidOperationException($"Unknown check type '{q.CheckType}'.");
        }
    }

    private static SqlCommand Command(SqlConnection conn, string sql, ExtractJobDef job, long loadId)
    {
        var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = Math.Max(30, job.CommandTimeoutSec);
        cmd.Parameters.AddWithValue("@job", job.ExtractJobId);
        cmd.Parameters.AddWithValue("@load", loadId);
        return cmd;
    }

    private static async Task<long?> ScalarAsync(SqlConnection conn, string sql, ExtractJobDef job, long loadId, CancellationToken ct)
    {
        await using var cmd = Command(conn, sql, job, loadId);
        var v = await cmd.ExecuteScalarAsync(ct);
        return v is null or DBNull ? null : Convert.ToInt64(v, CultureInfo.InvariantCulture);
    }

    private static string Truncate(string s) => s.Length <= 1000 ? s : s[..1000];
}
