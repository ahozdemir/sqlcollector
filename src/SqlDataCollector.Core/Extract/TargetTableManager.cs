using System.Data.Common;
using System.Globalization;
using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using SqlDataCollector.Infrastructure;
using SqlDataCollector.Models;
using SqlDataCollector.Options;

namespace SqlDataCollector.Extract;

/// <summary>Maps a source result-set column to the target column type declaration.</summary>
public sealed record TargetColumnType(string BaseType, int Length, string Declaration)
{
    /// <summary>Length: -1 = max, 0 = no length.</summary>
    public bool IsVariableLength => BaseType is "varchar" or "nvarchar" or "varbinary";
}

public static class SqlTypeMapper
{
    public static TargetColumnType Map(DbColumn col)
    {
        var t = (col.DataTypeName ?? "").ToLowerInvariant();
        if (t.Contains('.')) t = t[(t.LastIndexOf('.') + 1)..]; // UDT: "db.sys.geography"

        int size = col.ColumnSize ?? -1;
        switch (t)
        {
            case "bigint": case "int": case "smallint": case "tinyint": case "bit":
            case "float": case "real": case "money": case "smallmoney":
            case "date": case "datetime": case "smalldatetime":
            case "uniqueidentifier": case "xml": case "sql_variant":
                return new(t, 0, t);

            case "decimal": case "numeric":
            {
                int p = col.NumericPrecision is > 0 and <= 38 ? col.NumericPrecision.Value : 38;
                int s = col.NumericScale is >= 0 and <= 38 ? Math.Min(col.NumericScale.Value, p) : 0;
                return new("decimal", 0, $"decimal({p},{s})");
            }

            case "datetime2": case "time": case "datetimeoffset":
            {
                int scale = col.NumericScale is >= 0 and <= 7 ? col.NumericScale.Value : 7;
                return new(t, 0, $"{t}({scale})");
            }

            case "char": case "varchar": case "binary": case "varbinary":
                return Sized(t, size, 8000);

            case "nchar": case "nvarchar":
                return Sized(t, size, 4000);

            case "text": return new("varchar", -1, "varchar(max)");
            case "ntext": return new("nvarchar", -1, "nvarchar(max)");
            case "image": return new("varbinary", -1, "varbinary(max)");
            case "timestamp": case "rowversion": return new("binary", 8, "binary(8)");

            case "geography": case "geometry": case "hierarchyid":
                throw new NotSupportedException(
                    $"Column '{col.ColumnName}' is of type {t}. Convert it to text in the query: CAST({col.ColumnName}.ToString() AS nvarchar(max)) AS {col.ColumnName}");

            default:
                throw new NotSupportedException($"Unsupported type for column '{col.ColumnName}': {col.DataTypeName}");
        }
    }

    private static TargetColumnType Sized(string t, int size, int limit)
    {
        if (size <= 0 || size > limit)
        {
            var vt = t switch { "char" => "varchar", "nchar" => "nvarchar", "binary" => "varbinary", _ => t };
            return new(vt, -1, $"{vt}(max)");
        }
        return new(t, size, $"{t}({size})");
    }

    /// <summary>Builds a type declaration from sys.columns in the same format (for comparison).</summary>
    public static string Declaration(string type, int maxLength, int precision, int scale)
    {
        type = type.ToLowerInvariant();
        return type switch
        {
            "varchar" or "char" or "varbinary" or "binary" => maxLength == -1 ? $"{type}(max)" : $"{type}({maxLength})",
            "nvarchar" or "nchar" => maxLength == -1 ? $"{type}(max)" : $"{type}({maxLength / 2})",
            "decimal" or "numeric" => $"decimal({precision},{scale})",
            "datetime2" or "time" or "datetimeoffset" => $"{type}({scale})",
            "timestamp" => "binary(8)",
            _ => type
        };
    }
}

public sealed record ColumnDiff(string Column, string? SourceType, string? TargetType, string Kind);

/// <summary>Comparison of the source result set with the target table ("Check target").</summary>
public sealed class TableComparison
{
    public bool Exists { get; init; }
    public bool HasTechnicalColumns { get; init; }
    public bool IsPartitioned { get; init; }
    public List<ColumnDiff> Diffs { get; } = [];
    public IEnumerable<ColumnDiff> Missing => Diffs.Where(d => d.Kind == "Missing");
    public IEnumerable<ColumnDiff> TypeMismatch => Diffs.Where(d => d.Kind == "TypeMismatch");
    public IEnumerable<ColumnDiff> Extra => Diffs.Where(d => d.Kind == "Extra");
    public IEnumerable<ColumnDiff> Widenable => Diffs.Where(d => d.Kind == "Widen");

    /// <summary>Identical: technical columns present, no missing/mismatched/extra columns.</summary>
    public bool IsIdentical => Exists && HasTechnicalColumns && Diffs.Count == 0;

    public string Summary()
    {
        if (!Exists) return "Target table does not exist; it will be created on the first run.";
        if (IsIdentical) return "Target table matches the source exactly.";
        var sb = new StringBuilder();
        if (!HasTechnicalColumns) sb.Append("Technical columns (_LoadId, _LoadedAt, _ExtractJobId, _SourceServer) are missing. ");
        foreach (var d in Diffs)
            sb.Append(d.Kind switch
            {
                "Missing" => $"[{d.Column}] missing in target ({d.SourceType}). ",
                "Extra" => $"[{d.Column}] exists only in target ({d.TargetType}). ",
                "Widen" => $"[{d.Column}] too narrow in target: {d.TargetType} < {d.SourceType}. ",
                _ => $"[{d.Column}] type mismatch: source {d.SourceType}, target {d.TargetType}. "
            });
        return sb.ToString().Trim();
    }
}

/// <summary>
/// Checks / creates the target table. Strict: must match exactly. Evolve: adds new columns
/// and widens text/binary columns. Optional daily partitioning (_LoadedAt, UTC).
/// </summary>
public sealed class TargetTableManager(IOptions<CollectorOptions> options, ILogger<TargetTableManager> logger)
{
    private const int FutureDays = 7;

    private sealed record ExistingColumn(string Name, string Type, int MaxLength, int Precision, int Scale)
    {
        public string Declaration => SqlTypeMapper.Declaration(Type, MaxLength, Precision, Scale);
    }

    /// <returns>Map of source column name → actual target column name.</returns>
    public async Task<IReadOnlyDictionary<string, string>> EnsureAsync(
        SqlConnection conn, string schema, string table, IReadOnlyList<DbColumn> sourceColumns,
        SchemaPolicy policy, bool partitionDaily, CancellationToken ct, TargetStorage storage = TargetStorage.Rowstore)
    {
        ValidateSourceColumns(sourceColumns);
        var fullName = SqlIdent.Quote(schema, table);

        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        await LockDdlAsync(conn, tx, schema, table, ct);

        await ExecAsync(conn, tx,
            "IF SCHEMA_ID(@s) IS NULL BEGIN DECLARE @sql nvarchar(400) = N'CREATE SCHEMA ' + QUOTENAME(@s); EXEC(@sql); END",
            ct, ("@s", schema));

        var existing = await GetColumnsAsync(conn, tx, fullName, ct);
        if (existing.Count == 0)
        {
            await CreateTableAsync(conn, tx, schema, table, sourceColumns, partitionDaily, storage, ct);
        }
        else
        {
            var cmp = Compare(existing, sourceColumns, await IsPartitionedAsync(conn, tx, fullName, ct));
            if (policy == SchemaPolicy.Strict && !cmp.IsIdentical)
                throw new InvalidOperationException($"Strict schema policy: target {fullName} does not match the source. {cmp.Summary()}");
            if (policy == SchemaPolicy.Evolve)
                await EvolveAsync(conn, tx, fullName, existing, sourceColumns, ct);
        }

        var pf = await GetPartitionObjectsAsync(conn, tx, fullName, ct);
        if (pf is not null) await EnsureFutureBoundariesAsync(conn, tx, pf.Value.Function, pf.Value.Scheme, ct);

        existing = await GetColumnsAsync(conn, tx, fullName, ct);
        await tx.CommitAsync(ct);

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in sourceColumns) map[c.ColumnName] = existing[c.ColumnName].Name;
        foreach (var t in SqlIdent.TechnicalColumns) map[t] = existing[t].Name;
        return map;
    }

    /// <summary>UI "Check target": compares without changing anything.</summary>
    public async Task<TableComparison> CompareAsync(SqlConnection conn, string schema, string table,
        IReadOnlyList<DbColumn> sourceColumns, CancellationToken ct)
    {
        ValidateSourceColumns(sourceColumns);
        var fullName = SqlIdent.Quote(schema, table);
        var existing = await GetColumnsAsync(conn, null, fullName, ct);
        if (existing.Count == 0) return new TableComparison { Exists = false };
        return Compare(existing, sourceColumns, await IsPartitionedAsync(conn, null, fullName, ct));
    }

    private static TableComparison Compare(Dictionary<string, ExistingColumn> existing, IReadOnlyList<DbColumn> source, bool partitioned)
    {
        var result = new TableComparison
        {
            Exists = true,
            HasTechnicalColumns = SqlIdent.TechnicalColumns.All(existing.ContainsKey),
            IsPartitioned = partitioned
        };
        var srcNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in source)
        {
            srcNames.Add(c.ColumnName);
            var target = SqlTypeMapper.Map(c);
            if (!existing.TryGetValue(c.ColumnName, out var ex))
            {
                result.Diffs.Add(new(c.ColumnName, target.Declaration, null, "Missing"));
                continue;
            }
            if (string.Equals(ex.Declaration, target.Declaration, StringComparison.OrdinalIgnoreCase)) continue;

            var exType = ex.Type.ToLowerInvariant();
            bool widen = exType == target.BaseType && target.IsVariableLength && ex.MaxLength != -1;
            result.Diffs.Add(new(c.ColumnName, target.Declaration, ex.Declaration, widen ? "Widen" : "TypeMismatch"));
        }
        foreach (var ex in existing.Values)
            if (!srcNames.Contains(ex.Name) && !SqlIdent.IsTechnical(ex.Name))
                result.Diffs.Add(new(ex.Name, null, ex.Declaration, "Extra"));
        return result;
    }

    private static void ValidateSourceColumns(IReadOnlyList<DbColumn> cols)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in cols)
        {
            if (string.IsNullOrWhiteSpace(c.ColumnName))
                throw new InvalidOperationException($"The source result set has an unnamed column (ordinal {c.ColumnOrdinal}). Give computed columns an alias.");
            if (SqlIdent.IsTechnical(c.ColumnName))
                throw new InvalidOperationException($"'{c.ColumnName}' is a reserved technical column name; use a different alias in the source.");
            if (!seen.Add(c.ColumnName))
                throw new InvalidOperationException($"Duplicate column name in the source result set: '{c.ColumnName}'.");
        }
    }

    private async Task CreateTableAsync(SqlConnection conn, SqlTransaction tx, string schema, string table,
        IReadOnlyList<DbColumn> cols, bool partitionDaily, TargetStorage storage, CancellationToken ct, string? indexBase = null)
    {
        if (storage == TargetStorage.Columnstore) ValidateColumnstoreColumns(cols);
        var full = SqlIdent.Quote(schema, table);
        var safe = indexBase ?? Safe(schema + "_" + table);
        var sb = new StringBuilder();

        string onClause = "";
        if (partitionDaily)
        {
            var (pf, ps) = PartitionNames(schema, table);
            var today = DateTime.UtcNow.Date;
            var bounds = Enumerable.Range(-1, FutureDays + 2)
                .Select(d => $"'{today.AddDays(d).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}'");
            sb.AppendLine($"IF NOT EXISTS (SELECT 1 FROM sys.partition_functions WHERE name = N'{pf}')");
            sb.AppendLine($"    CREATE PARTITION FUNCTION {SqlIdent.Quote(pf)} (datetime2(3)) AS RANGE RIGHT FOR VALUES ({string.Join(", ", bounds)});");
            sb.AppendLine($"IF NOT EXISTS (SELECT 1 FROM sys.partition_schemes WHERE name = N'{ps}')");
            sb.AppendLine($"    CREATE PARTITION SCHEME {SqlIdent.Quote(ps)} AS PARTITION {SqlIdent.Quote(pf)} ALL TO ([PRIMARY]);");
            onClause = $" ON {SqlIdent.Quote(ps)}({SqlIdent.Quote(SqlIdent.LoadedAtColumn)})";
        }

        sb.AppendLine($"CREATE TABLE {full} (");
        sb.AppendLine($"    {SqlIdent.Quote(SqlIdent.LoadIdColumn)} bigint NOT NULL,");
        sb.AppendLine($"    {SqlIdent.Quote(SqlIdent.LoadedAtColumn)} datetime2(3) NOT NULL,");
        sb.AppendLine($"    {SqlIdent.Quote(SqlIdent.ExtractJobIdColumn)} int NOT NULL,");
        sb.Append($"    {SqlIdent.Quote(SqlIdent.SourceServerColumn)} nvarchar(100) NULL");
        foreach (var c in cols)
            sb.Append($",\n    {SqlIdent.Quote(c.ColumnName)} {SqlTypeMapper.Map(c).Declaration} NULL");
        sb.AppendLine($"\n){onClause};");

        var compression = options.Value.TargetTableCompression?.ToUpperInvariant() switch
        {
            "ROW" => " WITH (DATA_COMPRESSION = ROW)",
            "PAGE" => " WITH (DATA_COMPRESSION = PAGE)",
            _ => ""
        };
        var la = SqlIdent.Quote(SqlIdent.LoadedAtColumn);
        var li = SqlIdent.Quote(SqlIdent.LoadIdColumn);
        var jid = SqlIdent.Quote(SqlIdent.ExtractJobIdColumn);
        if (storage == TargetStorage.Columnstore)
        {
            // no other indexes: every load goes straight into compressed rowgroups; filters on _LoadId / _LoadedAt skip
            // rowgroups by their min/max values (loads arrive in order), retention truncates partitions or deletes
            sb.AppendLine($"CREATE CLUSTERED COLUMNSTORE INDEX {SqlIdent.Quote("CCI_" + safe)} ON {full}{onClause};");
        }
        else
        {
            sb.AppendLine($"CREATE CLUSTERED INDEX {SqlIdent.Quote("CIX_" + safe)} ON {full} ({la}, {li}){compression}{onClause};");
            sb.AppendLine($"CREATE NONCLUSTERED INDEX {SqlIdent.Quote("IX_" + safe + "_Job")} ON {full} ({jid}, {li}){compression}{onClause};");
            sb.AppendLine($"CREATE NONCLUSTERED INDEX {SqlIdent.Quote("IX_" + safe + "_LoadId")} ON {full} ({li}){compression}{onClause};");
        }

        await ExecAsync(conn, tx, sb.ToString(), ct);
        logger.LogInformation("Target table created: {Table} ({Count} columns, {Storage}, daily partitioning: {Part})", full, cols.Count, storage, partitionDaily);
    }

    /// <summary>Columnstore indexes cannot contain xml or sql_variant columns.</summary>
    public static void ValidateColumnstoreColumns(IReadOnlyList<DbColumn> cols)
    {
        var unsupported = cols.Where(c => SqlTypeMapper.Map(c).BaseType is "xml" or "sql_variant").Select(c => c.ColumnName).ToList();
        if (unsupported.Count > 0)
            throw new InvalidOperationException(
                $"A columnstore target table cannot hold xml or sql_variant columns ({string.Join(", ", unsupported)}). " +
                "Choose Rowstore storage or convert them in a query, e.g. CAST(col AS nvarchar(max)) AS col.");
    }

    /// <summary>True when the table has a clustered columnstore index (decides the bulk copy batch size).</summary>
    public static async Task<bool> IsColumnstoreAsync(SqlConnection conn, string schema, string table, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT CASE WHEN EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(@n, N'U') AND type = 5) THEN 1 ELSE 0 END;";
        cmd.Parameters.AddWithValue("@n", SqlIdent.Quote(schema, table));
        return (int)(await cmd.ExecuteScalarAsync(ct))! == 1;
    }

    private async Task EvolveAsync(SqlConnection conn, SqlTransaction tx, string full,
        Dictionary<string, ExistingColumn> existing, IReadOnlyList<DbColumn> cols, CancellationToken ct)
    {
        // Add technical columns if missing (table created outside this app)
        var techDefs = new (string Name, string Decl)[]
        {
            (SqlIdent.LoadIdColumn, "bigint NULL"), (SqlIdent.LoadedAtColumn, "datetime2(3) NULL"),
            (SqlIdent.ExtractJobIdColumn, "int NULL"), (SqlIdent.SourceServerColumn, "nvarchar(100) NULL")
        };
        foreach (var (name, decl) in techDefs)
            if (!existing.ContainsKey(name))
            {
                await ExecAsync(conn, tx, $"ALTER TABLE {full} ADD {SqlIdent.Quote(name)} {decl};", ct);
                logger.LogWarning("Evolve: added technical column {Column} to {Table}", full, name);
            }

        foreach (var c in cols)
        {
            var target = SqlTypeMapper.Map(c);
            if (!existing.TryGetValue(c.ColumnName, out var ex))
            {
                await ExecAsync(conn, tx, $"ALTER TABLE {full} ADD {SqlIdent.Quote(c.ColumnName)} {target.Declaration} NULL;", ct);
                logger.LogWarning("Evolve: added column {Column} {Type} to {Table}", full, c.ColumnName, target.Declaration);
                continue;
            }

            var exType = ex.Type.ToLowerInvariant();
            if (exType != target.BaseType || !target.IsVariableLength || ex.MaxLength == -1) continue;
            int exLen = exType == "nvarchar" ? ex.MaxLength / 2 : ex.MaxLength;
            if (target.Length != -1 && target.Length <= exLen) continue;

            await ExecAsync(conn, tx, $"ALTER TABLE {full} ALTER COLUMN {SqlIdent.Quote(ex.Name)} {target.Declaration} NULL;", ct);
            logger.LogWarning("Evolve: widened {Table}.{Column} {Old} -> {New}", full, ex.Name, ex.Declaration, target.Declaration);
        }
    }

    // ------------------------------------------------------------ partition maintenance

    public static (string Function, string Scheme) PartitionNames(string schema, string table)
    {
        var safe = Safe(schema + "_" + table);
        return ($"pf_{safe}_daily", $"ps_{safe}_daily");
    }

    private static async Task<(string Function, string Scheme)?> GetPartitionObjectsAsync(SqlConnection conn, SqlTransaction? tx, string full, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT TOP (1) pf.name, ps.name
            FROM sys.indexes i
            JOIN sys.partition_schemes ps ON ps.data_space_id = i.data_space_id
            JOIN sys.partition_functions pf ON pf.function_id = ps.function_id
            WHERE i.object_id = OBJECT_ID(@n, N'U') AND i.index_id IN (0, 1);
            """;
        cmd.Parameters.AddWithValue("@n", full);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? (r.GetString(0), r.GetString(1)) : null;
    }

    private static async Task<bool> IsPartitionedAsync(SqlConnection conn, SqlTransaction? tx, string full, CancellationToken ct) =>
        await GetPartitionObjectsAsync(conn, tx, full, ct) is not null;

    private static async Task<List<DateTime>> GetBoundariesAsync(SqlConnection conn, SqlTransaction? tx, string function, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT CAST(prv.value AS datetime2(3))
            FROM sys.partition_range_values prv
            JOIN sys.partition_functions pf ON pf.function_id = prv.function_id
            WHERE pf.name = @pf ORDER BY prv.boundary_id;
            """;
        cmd.Parameters.AddWithValue("@pf", function);
        var list = new List<DateTime>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(r.GetDateTime(0));
        return list;
    }

    /// <summary>Creates empty partitions for upcoming days (splitting the empty right-most partition is metadata-only).</summary>
    private async Task EnsureFutureBoundariesAsync(SqlConnection conn, SqlTransaction tx, string function, string scheme, CancellationToken ct)
    {
        var bounds = await GetBoundariesAsync(conn, tx, function, ct);
        var max = bounds.Count == 0 ? DateTime.UtcNow.Date : bounds[^1];
        var until = DateTime.UtcNow.Date.AddDays(FutureDays);
        while (max < until)
        {
            max = max.AddDays(1);
            await ExecAsync(conn, tx,
                $"ALTER PARTITION SCHEME {SqlIdent.Quote(scheme)} NEXT USED [PRIMARY]; " +
                $"ALTER PARTITION FUNCTION {SqlIdent.Quote(function)}() SPLIT RANGE ('{max.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}');", ct);
        }
    }

    /// <summary>
    /// Retention: TRUNCATEs partitions before the cutoff day and merges their boundaries.
    /// Returns false if the table is not partitioned (the caller falls back to DELETE).
    /// </summary>
    public async Task<(bool Partitioned, int Truncated)> TruncateOldPartitionsAsync(
        SqlConnection conn, string schema, string table, DateTime cutoffUtc, CancellationToken ct)
    {
        var full = SqlIdent.Quote(schema, table);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        await LockDdlAsync(conn, tx, schema, table, ct);
        var pf = await GetPartitionObjectsAsync(conn, tx, full, ct);
        if (pf is null) { await tx.CommitAsync(ct); return (false, 0); }

        var cutoffDay = cutoffUtc.Date;
        var bounds = await GetBoundariesAsync(conn, tx, pf.Value.Function, ct);
        // RANGE RIGHT: partition k = [b(k-1), b(k)). Partitions with b(k) <= cutoffDay are entirely old.
        int k = bounds.Count(b => b <= cutoffDay);
        if (k > 0)
        {
            await ExecAsync(conn, tx, $"TRUNCATE TABLE {full} WITH (PARTITIONS (1 TO {k}));", ct);
            foreach (var b in bounds.Take(k - 1))
                await ExecAsync(conn, tx,
                    $"ALTER PARTITION FUNCTION {SqlIdent.Quote(pf.Value.Function)}() MERGE RANGE ('{b.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}');", ct);
        }
        await EnsureFutureBoundariesAsync(conn, tx, pf.Value.Function, pf.Value.Scheme, ct);
        await tx.CommitAsync(ct);
        return (true, k);
    }

    // ------------------------------------------------------------ Merge: unique key index

    public static string MergeKeyIndexName(string schema, string table) => "UX_" + Safe(schema + "_" + table) + "_Key";

    /// <summary>
    /// Merge mode: makes sure [schema].[table] has the unique index UX_..._Key on exactly the key columns
    /// (created on the first run, re-created when the key changes). Fails clearly on duplicate keys,
    /// key columns that cannot be indexed and daily partitioned tables.
    /// </summary>
    public async Task EnsureMergeKeyAsync(SqlConnection conn, string schema, string table, IReadOnlyList<string> keyColumns, CancellationToken ct)
    {
        var full = SqlIdent.Quote(schema, table);
        var ix = MergeKeyIndexName(schema, table);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        await LockDdlAsync(conn, tx, schema, table, ct);

        if (await IsPartitionedAsync(conn, tx, full, ct))
            throw new InvalidOperationException($"Merge cannot write to the daily partitioned table {full}; choose another target table.");

        var columns = await GetColumnsAsync(conn, tx, full, ct);
        foreach (var k in keyColumns)
        {
            if (!columns.TryGetValue(k, out var col))
                throw new InvalidOperationException($"Merge key column '{k}' does not exist in {full}.");
            if (col.MaxLength == -1 || col.Type.ToLowerInvariant() is "xml" or "text" or "ntext" or "image")
                throw new InvalidOperationException(
                    $"Merge key column '{k}' is {col.Declaration} and cannot be part of an index. Cast it in the query to a sized type, e.g. CAST({k} AS nvarchar(200)) AS {k}.");
        }

        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                SELECT c.name FROM sys.indexes i
                JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.key_ordinal > 0
                JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                WHERE i.object_id = OBJECT_ID(@t, N'U') AND i.name = @ix ORDER BY ic.key_ordinal;
                """;
            cmd.Parameters.AddWithValue("@t", full);
            cmd.Parameters.AddWithValue("@ix", ix);
            var current = new List<string>();
            await using (var r = await cmd.ExecuteReaderAsync(ct))
                while (await r.ReadAsync(ct)) current.Add(r.GetString(0));
            if (current.SequenceEqual(keyColumns.Select(k => columns[k].Name), StringComparer.OrdinalIgnoreCase))
            {
                await tx.CommitAsync(ct);
                return;
            }
            if (current.Count > 0) await ExecAsync(conn, tx, $"DROP INDEX {SqlIdent.Quote(ix)} ON {full};", ct);
        }

        var compression = options.Value.TargetTableCompression?.ToUpperInvariant() switch
        {
            "ROW" => " WITH (DATA_COMPRESSION = ROW)",
            "PAGE" => " WITH (DATA_COMPRESSION = PAGE)",
            _ => ""
        };
        try
        {
            await ExecAsync(conn, tx,
                $"CREATE UNIQUE NONCLUSTERED INDEX {SqlIdent.Quote(ix)} ON {full} ({string.Join(", ", keyColumns.Select(k => SqlIdent.Quote(columns[k].Name)))}){compression};", ct);
        }
        catch (SqlException ex) when (ex.Number is 1505 or 1911 or 1919 or 1946)
        {
            throw new InvalidOperationException(ex.Number == 1505
                ? $"Merge: {full} already contains rows with the same key ({string.Join(", ", keyColumns)}), e.g. from earlier Append loads. " +
                  "Remove the duplicates or merge into a new table."
                : $"Merge: the unique index on ({string.Join(", ", keyColumns)}) cannot be created on {full}: {ex.Message}", ex);
        }
        await tx.CommitAsync(ct);
        logger.LogInformation("Merge key index {Index} created on {Table} ({Keys}).", ix, full, string.Join(", ", keyColumns));
    }

    // ------------------------------------------------------------ Replace: rebuild and swap

    public const string RebuildSuffix = "__sdc_new";
    public const string RebuildOldSuffix = "__sdc_old";

    /// <summary>Suggested target schema for a source database: lower case, letters/digits/underscore ("Sales DB" → "sales_db").</summary>
    public static string SuggestSchema(string? database)
    {
        // strip diacritics first (ş → s, ç → c, ü → u, İ → I), then lower case; dotless ı → i
        var plain = new string((database ?? "").Trim().Replace('ı', 'i').Normalize(NormalizationForm.FormD)
            .Where(ch => CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark).ToArray()).ToLowerInvariant();
        var s = new string(plain.Select(ch => char.IsAsciiLetterOrDigit(ch) ? ch : '_').ToArray()).Trim('_');
        while (s.Contains("__", StringComparison.Ordinal)) s = s.Replace("__", "_", StringComparison.Ordinal);
        if (s.Length == 0) return "stage";
        if (char.IsAsciiDigit(s[0])) s = "db_" + s;
        return s.Length > 128 ? s[..128] : s;
    }

    /// <summary>
    /// Rebuild mode: creates [schema].[table__sdc_new] from the current source schema (dropping a leftover one first).
    /// An existing target table must be a collector table (has _LoadId); anything else is never replaced.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, string>> PrepareRebuildAsync(
        SqlConnection conn, string schema, string table, IReadOnlyList<DbColumn> sourceColumns, CancellationToken ct,
        TargetStorage storage = TargetStorage.Rowstore)
    {
        ValidateSourceColumns(sourceColumns);
        if (table.Length + RebuildSuffix.Length > 128)
            throw new InvalidOperationException($"Rebuild needs a target table name of at most {128 - RebuildSuffix.Length} characters.");
        var staging = table + RebuildSuffix;

        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        await LockDdlAsync(conn, tx, schema, table, ct);
        await ExecAsync(conn, tx,
            "IF SCHEMA_ID(@s) IS NULL BEGIN DECLARE @sql nvarchar(400) = N'CREATE SCHEMA ' + QUOTENAME(@s); EXEC(@sql); END",
            ct, ("@s", schema));

        var existing = await GetColumnsAsync(conn, tx, SqlIdent.Quote(schema, table), ct);
        if (existing.Count > 0 && !existing.ContainsKey(SqlIdent.LoadIdColumn))
            throw new InvalidOperationException(
                $"Rebuild only replaces tables created by SqlDataCollector: {SqlIdent.Quote(schema, table)} exists without {SqlIdent.LoadIdColumn}. Choose another target table.");
        await DropCollectorTableAsync(conn, tx, schema, staging, ct);

        // index names as for the final table: index names are per table, so after the rename they are the usual ones
        await CreateTableAsync(conn, tx, schema, staging, sourceColumns, partitionDaily: false, storage, ct, indexBase: Safe(schema + "_" + table));
        var cols = await GetColumnsAsync(conn, tx, SqlIdent.Quote(schema, staging), ct);
        await tx.CommitAsync(ct);

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in sourceColumns) map[c.ColumnName] = cols[c.ColumnName].Name;
        foreach (var t in SqlIdent.TechnicalColumns) map[t] = cols[t].Name;
        return map;
    }

    /// <summary>Atomically replaces [schema].[table] with the loaded [table__sdc_new] (rename, rename, drop) in one short transaction.</summary>
    public async Task SwapRebuiltAsync(SqlConnection conn, string schema, string table, CancellationToken ct)
    {
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        await LockDdlAsync(conn, tx, schema, table, ct);
        var full = SqlIdent.Quote(schema, table);
        if ((await GetColumnsAsync(conn, tx, full, ct)).Count > 0)
        {
            await DropCollectorTableAsync(conn, tx, schema, table + RebuildOldSuffix, ct);
            await ExecAsync(conn, tx, "EXEC sp_rename @objname = @o, @newname = @n;", ct, ("@o", full), ("@n", table + RebuildOldSuffix));
        }
        await ExecAsync(conn, tx, "EXEC sp_rename @objname = @o, @newname = @n;", ct, ("@o", SqlIdent.Quote(schema, table + RebuildSuffix)), ("@n", table));
        await ExecAsync(conn, tx, $"IF OBJECT_ID(@o, N'U') IS NOT NULL DROP TABLE {SqlIdent.Quote(schema, table + RebuildOldSuffix)};", ct,
            ("@o", SqlIdent.Quote(schema, table + RebuildOldSuffix)));
        await tx.CommitAsync(ct);
        logger.LogInformation("Rebuild: {Table} swapped with the freshly loaded table.", full);
    }

    /// <summary>Drops the staging table of a failed rebuild (best effort is up to the caller).</summary>
    public async Task DropRebuildStagingAsync(SqlConnection conn, string schema, string table, CancellationToken ct)
    {
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        await LockDdlAsync(conn, tx, schema, table, ct);
        await DropCollectorTableAsync(conn, tx, schema, table + RebuildSuffix, ct);
        await tx.CommitAsync(ct);
    }

    /// <summary>Drops a table only if it is a collector table (has _LoadId).</summary>
    private static Task DropCollectorTableAsync(SqlConnection conn, SqlTransaction tx, string schema, string table, CancellationToken ct) =>
        ExecAsync(conn, tx, $"IF COL_LENGTH(@o, @c) IS NOT NULL DROP TABLE {SqlIdent.Quote(schema, table)};", ct,
            ("@o", SqlIdent.Quote(schema, table)), ("@c", SqlIdent.LoadIdColumn));

    // ------------------------------------------------------------ helpers

    private static string Safe(string s) => new(s.Select(ch => char.IsAsciiLetterOrDigit(ch) ? ch : '_').ToArray());

    private static Task LockDdlAsync(SqlConnection conn, SqlTransaction tx, string schema, string table, CancellationToken ct) =>
        ExecAsync(conn, tx, "EXEC sp_getapplock @Resource = @r, @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 600000;",
            ct, ("@r", $"etl:ddl:{schema}.{table}"));

    private static async Task<Dictionary<string, ExistingColumn>> GetColumnsAsync(SqlConnection conn, SqlTransaction? tx, string full, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT c.name, t.name, c.max_length, c.precision, c.scale
            FROM sys.columns c JOIN sys.types t ON t.user_type_id = c.user_type_id
            WHERE c.object_id = OBJECT_ID(@n, N'U');
            """;
        cmd.Parameters.AddWithValue("@n", full);
        var result = new Dictionary<string, ExistingColumn>(StringComparer.OrdinalIgnoreCase);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            result[r.GetString(0)] = new ExistingColumn(r.GetString(0), r.GetString(1), r.GetInt16(2), r.GetByte(3), r.GetByte(4));
        return result;
    }

    private static async Task ExecAsync(SqlConnection conn, SqlTransaction tx, string sql, CancellationToken ct,
        params (string Name, object Value)[] parameters)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.CommandTimeout = 0;
        foreach (var (n, v) in parameters) cmd.Parameters.AddWithValue(n, v);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
