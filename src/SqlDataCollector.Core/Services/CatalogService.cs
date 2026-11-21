using System.Data;
using System.Data.Common;
using Microsoft.Data.SqlClient;
using SqlDataCollector.Extract;
using SqlDataCollector.Infrastructure;
using SqlDataCollector.Models;

namespace SqlDataCollector.Services;

public sealed record TableInfo(string Schema, string Name, string Type, long? Rows)
{
    public string Key => $"{Schema}.{Name}";
}

/// <summary>A source column; KeyOrdinal = position in the primary key (0 = not part of it).</summary>
public sealed record ColumnInfo(string Name, string TypeDeclaration, bool Nullable, bool IsIdentity, int KeyOrdinal = 0);

public sealed record PreviewResult(IReadOnlyList<string> Columns, IReadOnlyList<string?[]> Rows, IReadOnlyList<ColumnInfo> Schema);

public sealed record ConnectionTestResult(bool Ok, string Message);

/// <summary>A collector table (has _LoadId and _ExtractJobId) that no extract job writes to any more.</summary>
public sealed record OrphanTable(string ServerName, string Database, string Schema, string Table, long? Rows);

/// <summary>
/// Server/database/table/column listing, preview and target comparison for the UI.
/// Connections use the app's service account; user permissions are filtered by AccessService.
/// </summary>
public sealed class CatalogService(SqlConnectionFactory factory, MetadataRepository repo, TargetTableManager tables)
{
    private const int UiTimeoutSec = 60;

    private async Task<SqlServerDef> ServerAsync(int id) =>
        await repo.GetServerAsync(id) ?? throw new InvalidOperationException($"Server not found (Id={id}).");

    public async Task<ConnectionTestResult> TestAsync(SqlServerDef server, CancellationToken ct = default)
    {
        try
        {
            await using var c = await factory.OpenAsync(server, "master", forWrite: false, ct);
            await using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT CONCAT(@@SERVERNAME, ' | ', CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(50)), ' ', CAST(SERVERPROPERTY('Edition') AS nvarchar(100)), ' | login: ', SUSER_SNAME())";
            return new(true, (string)(await cmd.ExecuteScalarAsync(ct))!);
        }
        catch (Exception ex)
        {
            return new(false, ex.Message);
        }
    }

    /// <summary>
    /// Collector tables in the target databases used by jobs that no extract job (active or not) writes to – e.g. left
    /// behind by deleted jobs. Retention never touches them. Read-only: listing only, nothing is dropped.
    /// </summary>
    public async Task<(IReadOnlyList<OrphanTable> Tables, IReadOnlyList<string> Errors)> OrphanTablesAsync(CancellationToken ct = default)
    {
        var servers = (await repo.GetServersAsync()).Where(s => s.IsActive && s.IsTarget).ToList();
        var extracts = await repo.GetExtractJobsAsync();
        var exports = await repo.GetExportJobsAsync();
        var tables = new List<OrphanTable>();
        var errors = new List<string>();
        foreach (var server in servers)
        {
            var dbs = extracts.Where(j => j.TargetServerId == server.SqlServerId).Select(j => j.TargetDatabase)
                .Concat(exports.Where(e => e.SourceServerId == server.SqlServerId).Select(e => e.SourceDatabase))
                .Distinct(StringComparer.OrdinalIgnoreCase).Order().ToList();
            foreach (var db in dbs)
            {
                var used = extracts.Where(j => j.TargetServerId == server.SqlServerId && j.TargetDatabase.Equals(db, StringComparison.OrdinalIgnoreCase))
                    .Select(j => $"{j.TargetSchema}.{j.TargetTable}").ToHashSet(StringComparer.OrdinalIgnoreCase);
                try
                {
                    await using var c = await factory.OpenAsync(server, db, forWrite: false, ct);
                    await using var cmd = c.CreateCommand();
                    cmd.CommandTimeout = UiTimeoutSec;
                    cmd.CommandText = """
                        SELECT s.name, t.name, (SELECT SUM(p.rows) FROM sys.partitions p WHERE p.object_id = t.object_id AND p.index_id IN (0,1))
                        FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id
                        WHERE EXISTS (SELECT 1 FROM sys.columns c WHERE c.object_id = t.object_id AND c.name = @load)
                          AND EXISTS (SELECT 1 FROM sys.columns c WHERE c.object_id = t.object_id AND c.name = @job)
                        ORDER BY s.name, t.name;
                        """;
                    cmd.Parameters.AddWithValue("@load", SqlIdent.LoadIdColumn);
                    cmd.Parameters.AddWithValue("@job", SqlIdent.ExtractJobIdColumn);
                    await using var r = await cmd.ExecuteReaderAsync(ct);
                    while (await r.ReadAsync(ct))
                    {
                        var (schema, name) = (r.GetString(0), r.GetString(1));
                        if (!used.Contains($"{schema}.{name}"))
                            tables.Add(new OrphanTable(server.Name, db, schema, name, r.IsDBNull(2) ? null : r.GetInt64(2)));
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    errors.Add($"{server.Name}/{db}: {ex.Message}");
                }
            }
        }
        return (tables, errors);
    }

    /// <summary>Whether SNAPSHOT isolation reads are allowed in a database (ALLOW_SNAPSHOT_ISOLATION ON).</summary>
    public async Task<bool> SnapshotAllowedAsync(int serverId, string database, CancellationToken ct = default)
    {
        await using var c = await factory.OpenAsync(await ServerAsync(serverId), "master", forWrite: false, ct);
        await using var cmd = c.CreateCommand();
        cmd.CommandTimeout = UiTimeoutSec;
        cmd.CommandText = "SELECT snapshot_isolation_state FROM sys.databases WHERE name = @db;";
        cmd.Parameters.AddWithValue("@db", database);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct) ?? 0) == 1;
    }

    public async Task<IReadOnlyList<string>> DatabasesAsync(int serverId, CancellationToken ct = default)
    {
        await using var c = await factory.OpenAsync(await ServerAsync(serverId), "master", forWrite: false, ct);
        await using var cmd = c.CreateCommand();
        cmd.CommandTimeout = UiTimeoutSec;
        cmd.CommandText = """
            SELECT name FROM sys.databases
            WHERE database_id > 4 AND state_desc = 'ONLINE' AND HAS_DBACCESS(name) = 1
            ORDER BY name;
            """;
        var list = new List<string>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(r.GetString(0));
        return list;
    }

    public async Task<IReadOnlyList<TableInfo>> TablesAsync(int serverId, string database, CancellationToken ct = default)
    {
        await using var c = await factory.OpenAsync(await ServerAsync(serverId), database, forWrite: false, ct);
        await using var cmd = c.CreateCommand();
        cmd.CommandTimeout = UiTimeoutSec;
        cmd.CommandText = """
            SELECT s.name, o.name, o.type_desc,
                   (SELECT SUM(p.rows) FROM sys.partitions p WHERE p.object_id = o.object_id AND p.index_id IN (0,1))
            FROM sys.objects o JOIN sys.schemas s ON s.schema_id = o.schema_id
            WHERE o.type IN ('U','V') AND o.is_ms_shipped = 0
            ORDER BY s.name, o.name;
            """;
        var list = new List<TableInfo>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new(r.GetString(0), r.GetString(1), r.GetString(2) == "VIEW" ? "View" : "Table", r.IsDBNull(3) ? null : r.GetInt64(3)));
        return list;
    }

    public async Task<IReadOnlyList<string>> SchemasAsync(int serverId, string database, CancellationToken ct = default)
    {
        await using var c = await factory.OpenAsync(await ServerAsync(serverId), database, forWrite: false, ct);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT name FROM sys.schemas WHERE schema_id < 16384 AND name NOT IN ('sys','INFORMATION_SCHEMA','guest') ORDER BY name;";
        var list = new List<string>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(r.GetString(0));
        return list;
    }

    public async Task<IReadOnlyList<ColumnInfo>> ColumnsAsync(int serverId, string database, string schema, string table, CancellationToken ct = default)
    {
        await using var c = await factory.OpenAsync(await ServerAsync(serverId), database, forWrite: false, ct);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT c.name, t.name, c.max_length, c.precision, c.scale, c.is_nullable, c.is_identity,
                   CAST(ISNULL((SELECT ic.key_ordinal FROM sys.indexes i
                                JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
                                WHERE i.object_id = c.object_id AND i.is_primary_key = 1 AND ic.column_id = c.column_id), 0) AS int)
            FROM sys.columns c JOIN sys.types t ON t.user_type_id = c.user_type_id
            WHERE c.object_id = OBJECT_ID(@n) ORDER BY c.column_id;
            """;
        cmd.Parameters.AddWithValue("@n", SqlIdent.Quote(schema, table));
        var list = new List<ColumnInfo>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new(r.GetString(0), SqlTypeMapper.Declaration(r.GetString(1), r.GetInt16(2), r.GetByte(3), r.GetByte(4)),
                r.GetBoolean(5), r.GetBoolean(6), r.GetInt32(7)));
        return list;
    }

    /// <summary>Returns the first N rows and the result schema of the source table/query.</summary>
    public async Task<PreviewResult> PreviewExtractAsync(ExtractJobDef job, int maxRows = 50, CancellationToken ct = default)
    {
        await using var c = await factory.OpenAsync(await ServerAsync(job.SourceServerId), job.SourceDatabase, forWrite: false, ct);
        await using var cmd = BuildSourceCommand(c, job);
        return await ReadPreviewAsync(cmd, maxRows, ct);
    }

    /// <summary>Export preview: runs as the read-only Collector:ExportReadAsUser, exactly like the export itself.</summary>
    public async Task<PreviewResult> PreviewExportSqlAsync(int serverId, string database, string sql, IEnumerable<SqlParameter>? parameters, int maxRows = 50, CancellationToken ct = default)
    {
        await using var session = await factory.OpenExportReadAsync(await ServerAsync(serverId), database, forWrite: false, ct);
        await using var cmd = session.Connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = UiTimeoutSec;
        if (parameters is not null) foreach (var p in parameters) cmd.Parameters.Add(p);
        return await ReadPreviewAsync(cmd, maxRows, ct);
    }

    /// <summary>"Check target": compares the source result schema with the target table.</summary>
    public async Task<TableComparison> CompareTargetAsync(ExtractJobDef job, CancellationToken ct = default)
    {
        IReadOnlyList<DbColumn> sourceSchema;
        await using (var src = await factory.OpenAsync(await ServerAsync(job.SourceServerId), job.SourceDatabase, forWrite: false, ct))
            sourceSchema = await GetResultSchemaAsync(src, job, ct);

        await using var tgt = await factory.OpenAsync(await ServerAsync(job.TargetServerId), job.TargetDatabase, forWrite: false, ct);
        return await tables.CompareAsync(tgt, job.TargetSchema, job.TargetTable, sourceSchema, ct);
    }

    private static SqlCommand BuildSourceCommand(SqlConnection c, ExtractJobDef job)
    {
        var cmd = c.CreateCommand();
        cmd.CommandText = SourceCommandBuilder.BuildSql(job, hasWatermark: false);
        cmd.CommandTimeout = UiTimeoutSec;
        if (job.UsesWatermark() && job.SourceType == SourceType.Query)
            cmd.Parameters.Add(Watermark.ToParameter(SourceCommandBuilder.WatermarkParam, null, null));
        return cmd;
    }

    private static async Task<IReadOnlyList<DbColumn>> GetResultSchemaAsync(SqlConnection c, ExtractJobDef job, CancellationToken ct)
    {
        // Try SchemaOnly first (no data read); procedures using temp tables fail that way, so execute and cancel immediately
        try
        {
            await using var cmd = BuildSourceCommand(c, job);
            await using var r = await cmd.ExecuteReaderAsync(CommandBehavior.SchemaOnly, ct);
            while (r.FieldCount == 0 && await r.NextResultAsync(ct)) { }
            if (r.FieldCount > 0) return r.GetColumnSchema();
        }
        catch (SqlException) { }

        await using var cmd2 = BuildSourceCommand(c, job);
        await using var r2 = await cmd2.ExecuteReaderAsync(ct);
        while (r2.FieldCount == 0 && await r2.NextResultAsync(ct)) { }
        if (r2.FieldCount == 0) throw new InvalidOperationException("The source query returned no result set.");
        var schema = r2.GetColumnSchema();
        cmd2.Cancel();
        return schema;
    }

    private static async Task<PreviewResult> ReadPreviewAsync(SqlCommand cmd, int maxRows, CancellationToken ct)
    {
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (r.FieldCount == 0 && await r.NextResultAsync(ct)) { }
        if (r.FieldCount == 0) throw new InvalidOperationException("The query returned no result set.");

        var schema = r.GetColumnSchema();
        var cols = schema.Select(s => s.ColumnName).ToList();
        var info = schema.Select(s =>
        {
            string decl;
            try { decl = SqlTypeMapper.Map(s).Declaration; } catch (NotSupportedException) { decl = s.DataTypeName ?? "?"; }
            return new ColumnInfo(s.ColumnName, decl, s.AllowDBNull ?? true, s.IsIdentity ?? false);
        }).ToList();

        var rows = new List<string?[]>();
        while (rows.Count < maxRows && await r.ReadAsync(ct))
        {
            var row = new string?[r.FieldCount];
            for (int i = 0; i < r.FieldCount; i++)
            {
                if (r.IsDBNull(i)) { row[i] = null; continue; }
                try { row[i] = Export.CsvValueFormatter.For(r.GetDataTypeName(i))(r, i); }
                catch { row[i] = r.GetValue(i)?.ToString(); }
                if (row[i]!.Length > 200) row[i] = row[i]![..200] + "…";
            }
            rows.Add(row);
        }
        cmd.Cancel(); // do not pull the remaining rows from the server
        return new(cols, rows, info);
    }
}
