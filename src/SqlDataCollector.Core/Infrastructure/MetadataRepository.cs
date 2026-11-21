using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;
using SqlDataCollector.Models;

namespace SqlDataCollector.Infrastructure;

public sealed class MetadataRepository(SqlConnectionFactory factory)
{
    private static readonly string HostName = Environment.MachineName;

    private const string ServerSelect = """
        SELECT SqlServerId, Name, ServerAddress, IsSource, IsTarget, EncryptMode, TrustServerCertificate,
               ReadOnlyIntent, MultiSubnetFailover, ConnectTimeoutSec, Description, IsActive, ModifiedBy, ModifiedAtUtc
        FROM etl.SqlServer
        """;

    private const string ExtractSelect = """
        SELECT j.ExtractJobId, j.Name, j.SourceServerId, ss.Name AS SourceServerName, j.SourceDatabase,
               j.SourceType, j.SourceSchema, j.SourceTable, j.ColumnList, j.FilterClause, j.SourceQuery, j.UseNoLock,
               j.LoadMode, j.WatermarkColumn, j.SchemaPolicy, j.PartitionDaily, j.TargetServerId, ts.Name AS TargetServerName, j.TargetDatabase,
               j.TargetSchema, j.TargetTable, j.CronExpression, j.TimeZoneId, j.RetentionDays, j.CommandTimeoutSec,
               j.BatchSize, j.NotifyEmails, j.ReplaceRebuild, j.KeyColumns, j.MergeDeleteMissing, j.TriggerMode, j.IsActive, j.CreatedBy, j.ModifiedBy, j.ModifiedAtUtc
        FROM etl.ExtractJob j
        JOIN etl.SqlServer ss ON ss.SqlServerId = j.SourceServerId
        JOIN etl.SqlServer ts ON ts.SqlServerId = j.TargetServerId
        """;

    private const string ExportSelect = """
        SELECT e.ExportJobId, e.Name, e.ExportMode, e.SourceServerId, s.Name AS SourceServerName, e.SourceDatabase,
               e.SourceSchema, e.SourceTable, e.SourceQuery, e.ColumnList, e.IncludeTechnicalColumns, e.CronExpression,
               e.TimeZoneId, e.Bucket, e.ObjectNameTemplate, e.Delimiter, e.QuoteMode, e.NullValue, e.LineEnding,
               e.Utf8Bom, e.Gzip, e.MaxRowsPerFile, e.UploadWhenEmpty, e.WriteManifest, e.CommandTimeoutSec,
               e.NotifyEmails, e.TriggerMode, e.IsActive, e.CreatedBy, e.ModifiedBy, e.ModifiedAtUtc
        FROM etl.ExportJob e
        JOIN etl.SqlServer s ON s.SqlServerId = e.SourceServerId
        """;

    private SqlConnection Conn() => factory.Metadata();

    // ================================================================ servers

    public async Task<IReadOnlyList<SqlServerDef>> GetServersAsync()
    {
        await using var c = Conn();
        return (await c.QueryAsync<SqlServerDef>(ServerSelect + " ORDER BY Name")).AsList();
    }

    public async Task<SqlServerDef?> GetServerAsync(int id)
    {
        await using var c = Conn();
        return await c.QuerySingleOrDefaultAsync<SqlServerDef>(ServerSelect + " WHERE SqlServerId = @id", new { id });
    }

    public async Task<int> SaveServerAsync(SqlServerDef s, string user)
    {
        await using var c = Conn();
        s.ModifiedBy = user;
        int id;
        if (s.SqlServerId == 0)
            id = await c.ExecuteScalarAsync<int>("""
                INSERT etl.SqlServer (Name, ServerAddress, IsSource, IsTarget, EncryptMode, TrustServerCertificate, ReadOnlyIntent,
                                      MultiSubnetFailover, ConnectTimeoutSec, Description, IsActive, ModifiedBy)
                OUTPUT inserted.SqlServerId
                VALUES (@Name, @ServerAddress, @IsSource, @IsTarget, @EncryptMode, @TrustServerCertificate, @ReadOnlyIntent,
                        @MultiSubnetFailover, @ConnectTimeoutSec, @Description, @IsActive, @ModifiedBy);
                """, s);
        else
        {
            await c.ExecuteAsync("""
                UPDATE etl.SqlServer SET Name = @Name, ServerAddress = @ServerAddress, IsSource = @IsSource, IsTarget = @IsTarget,
                       EncryptMode = @EncryptMode, TrustServerCertificate = @TrustServerCertificate, ReadOnlyIntent = @ReadOnlyIntent,
                       MultiSubnetFailover = @MultiSubnetFailover, ConnectTimeoutSec = @ConnectTimeoutSec, Description = @Description,
                       IsActive = @IsActive, ModifiedBy = @ModifiedBy, ModifiedAtUtc = SYSUTCDATETIME()
                WHERE SqlServerId = @SqlServerId;
                """, s);
            id = s.SqlServerId;
        }
        await AuditAsync(user, s.SqlServerId == 0 ? "Create" : "Update", "SqlServer", id, s);
        return id;
    }

    public async Task DeleteServerAsync(int id, string user)
    {
        await using var c = Conn();
        await c.ExecuteAsync("DELETE etl.SqlServer WHERE SqlServerId = @id", new { id });
        await AuditAsync(user, "Delete", "SqlServer", id, null);
    }

    // ================================================================ roles & permissions

    public async Task<IReadOnlyList<RoleAssignmentDef>> GetRoleAssignmentsAsync()
    {
        await using var c = Conn();
        return (await c.QueryAsync<RoleAssignmentDef>(
            "SELECT RoleAssignmentId, PrincipalName, PrincipalType, Role, ModifiedBy, ModifiedAtUtc FROM etl.RoleAssignment ORDER BY Role, PrincipalName")).AsList();
    }

    public async Task SaveRoleAssignmentAsync(RoleAssignmentDef r, string user)
    {
        await using var c = Conn();
        r.ModifiedBy = user;
        await c.ExecuteAsync("""
            IF EXISTS (SELECT 1 FROM etl.RoleAssignment WHERE PrincipalName = @PrincipalName AND Role = @Role)
                UPDATE etl.RoleAssignment SET PrincipalType = @PrincipalType, ModifiedBy = @ModifiedBy, ModifiedAtUtc = SYSUTCDATETIME()
                WHERE PrincipalName = @PrincipalName AND Role = @Role;
            ELSE
                INSERT etl.RoleAssignment (PrincipalName, PrincipalType, Role, ModifiedBy) VALUES (@PrincipalName, @PrincipalType, @Role, @ModifiedBy);
            """, r);
        await AuditAsync(user, "Grant", "Role", null, r);
    }

    public async Task DeleteRoleAssignmentAsync(int id, string user)
    {
        await using var c = Conn();
        await c.ExecuteAsync("DELETE etl.RoleAssignment WHERE RoleAssignmentId = @id", new { id });
        await AuditAsync(user, "Revoke", "Role", id, null);
    }

    public async Task<IReadOnlyList<ServerPermissionDef>> GetServerPermissionsAsync()
    {
        await using var c = Conn();
        return (await c.QueryAsync<ServerPermissionDef>("""
            SELECT p.ServerPermissionId, p.PrincipalName, p.SqlServerId, s.Name AS ServerName, p.DatabaseName, p.CanSource, p.CanTarget,
                   p.ModifiedBy, p.ModifiedAtUtc
            FROM etl.ServerPermission p JOIN etl.SqlServer s ON s.SqlServerId = p.SqlServerId
            ORDER BY p.PrincipalName, s.Name, p.DatabaseName
            """)).AsList();
    }

    public async Task SaveServerPermissionAsync(ServerPermissionDef p, string user)
    {
        await using var c = Conn();
        p.ModifiedBy = user;
        p.DatabaseName = string.IsNullOrWhiteSpace(p.DatabaseName) ? null : p.DatabaseName.Trim();
        await c.ExecuteAsync("""
            IF EXISTS (SELECT 1 FROM etl.ServerPermission WHERE PrincipalName = @PrincipalName AND SqlServerId = @SqlServerId
                       AND ((DatabaseName IS NULL AND @DatabaseName IS NULL) OR DatabaseName = @DatabaseName))
                UPDATE etl.ServerPermission SET CanSource = @CanSource, CanTarget = @CanTarget, ModifiedBy = @ModifiedBy, ModifiedAtUtc = SYSUTCDATETIME()
                WHERE PrincipalName = @PrincipalName AND SqlServerId = @SqlServerId
                  AND ((DatabaseName IS NULL AND @DatabaseName IS NULL) OR DatabaseName = @DatabaseName);
            ELSE
                INSERT etl.ServerPermission (PrincipalName, SqlServerId, DatabaseName, CanSource, CanTarget, ModifiedBy)
                VALUES (@PrincipalName, @SqlServerId, @DatabaseName, @CanSource, @CanTarget, @ModifiedBy);
            """, p);
        await AuditAsync(user, "Grant", "ServerPermission", null, p);
    }

    public async Task DeleteServerPermissionAsync(int id, string user)
    {
        await using var c = Conn();
        await c.ExecuteAsync("DELETE etl.ServerPermission WHERE ServerPermissionId = @id", new { id });
        await AuditAsync(user, "Revoke", "ServerPermission", id, null);
    }

    /// <summary>All principals referenced by the app (AD groups are filtered with this at login).</summary>
    public async Task<HashSet<string>> GetKnownPrincipalsAsync()
    {
        await using var c = Conn();
        var names = await c.QueryAsync<string>("SELECT PrincipalName FROM etl.RoleAssignment UNION SELECT PrincipalName FROM etl.ServerPermission");
        return new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
    }

    // ================================================================ extract jobs

    public async Task<IReadOnlyList<ExtractJobDef>> GetExtractJobsAsync()
    {
        await using var c = Conn();
        return (await c.QueryAsync<ExtractJobDef>(ExtractSelect + " ORDER BY j.Name")).AsList();
    }

    public async Task<ExtractJobDef?> GetExtractJobAsync(int id)
    {
        await using var c = Conn();
        return await c.QuerySingleOrDefaultAsync<ExtractJobDef>(ExtractSelect + " WHERE j.ExtractJobId = @id", new { id });
    }

    public async Task<int> SaveExtractJobAsync(ExtractJobDef j, string user)
    {
        await using var c = Conn();
        j.ModifiedBy = user;
        bool isNew = j.ExtractJobId == 0;
        if (isNew)
        {
            j.CreatedBy = user;
            j.ExtractJobId = await c.ExecuteScalarAsync<int>("""
                INSERT etl.ExtractJob (Name, SourceServerId, SourceDatabase, SourceType, SourceSchema, SourceTable, ColumnList, FilterClause,
                    SourceQuery, UseNoLock, LoadMode, WatermarkColumn, SchemaPolicy, PartitionDaily, TargetServerId, TargetDatabase, TargetSchema, TargetTable,
                    CronExpression, TimeZoneId, RetentionDays, CommandTimeoutSec, BatchSize, NotifyEmails, ReplaceRebuild, KeyColumns, MergeDeleteMissing, TriggerMode, IsActive, CreatedBy, ModifiedBy)
                OUTPUT inserted.ExtractJobId
                VALUES (@Name, @SourceServerId, @SourceDatabase, @SourceType, @SourceSchema, @SourceTable, @ColumnList, @FilterClause,
                    @SourceQuery, @UseNoLock, @LoadMode, @WatermarkColumn, @SchemaPolicy, @PartitionDaily, @TargetServerId, @TargetDatabase, @TargetSchema, @TargetTable,
                    @CronExpression, @TimeZoneId, @RetentionDays, @CommandTimeoutSec, @BatchSize, @NotifyEmails, @ReplaceRebuild, @KeyColumns, @MergeDeleteMissing, @TriggerMode, @IsActive, @CreatedBy, @ModifiedBy);
                """, j);
        }
        else
        {
            await c.ExecuteAsync("""
                UPDATE etl.ExtractJob SET Name = @Name, SourceServerId = @SourceServerId, SourceDatabase = @SourceDatabase,
                    SourceType = @SourceType, SourceSchema = @SourceSchema, SourceTable = @SourceTable, ColumnList = @ColumnList,
                    FilterClause = @FilterClause, SourceQuery = @SourceQuery, UseNoLock = @UseNoLock, LoadMode = @LoadMode,
                    WatermarkColumn = @WatermarkColumn, SchemaPolicy = @SchemaPolicy, PartitionDaily = @PartitionDaily, TargetServerId = @TargetServerId, TargetDatabase = @TargetDatabase,
                    TargetSchema = @TargetSchema, TargetTable = @TargetTable, CronExpression = @CronExpression, TimeZoneId = @TimeZoneId,
                    RetentionDays = @RetentionDays, CommandTimeoutSec = @CommandTimeoutSec, BatchSize = @BatchSize, NotifyEmails = @NotifyEmails, ReplaceRebuild = @ReplaceRebuild, KeyColumns = @KeyColumns, MergeDeleteMissing = @MergeDeleteMissing, TriggerMode = @TriggerMode, IsActive = @IsActive,
                    ModifiedBy = @ModifiedBy, ModifiedAtUtc = SYSUTCDATETIME()
                WHERE ExtractJobId = @ExtractJobId;
                """, j);
        }
        await AuditAsync(user, isNew ? "Create" : "Update", "ExtractJob", j.ExtractJobId, j);
        return j.ExtractJobId;
    }

    public async Task DeleteExtractJobAsync(int id, string user)
    {
        await using var c = Conn();
        await c.ExecuteAsync(DeleteDependenciesSql + "DELETE etl.ExtractJob WHERE ExtractJobId = @id;", new { id, kind = "Extract" });
        await AuditAsync(user, "Delete", "ExtractJob", id, null);
    }

    public async Task ResetWatermarkAsync(int jobId, string user)
    {
        await using var c = Conn();
        await c.ExecuteAsync("DELETE etl.ExtractState WHERE ExtractJobId = @jobId", new { jobId });
        await AuditAsync(user, "ResetWatermark", "ExtractJob", jobId, null);
    }

    public async Task<ExtractState?> GetExtractStateAsync(int jobId)
    {
        await using var c = Conn();
        return await c.QuerySingleOrDefaultAsync<ExtractState>(
            "SELECT WatermarkType, WatermarkValue FROM etl.ExtractState WHERE ExtractJobId = @jobId", new { jobId });
    }

    public async Task<long> StartLoadRunAsync(ExtractJobDef job, string? watermarkFrom, string? triggeredBy)
    {
        await using var c = Conn();
        return await c.ExecuteScalarAsync<long>("""
            INSERT etl.LoadRun (ExtractJobId, TargetServerId, TargetDatabase, TargetSchema, TargetTable, Status, WatermarkFrom, HostName, TriggeredBy)
            OUTPUT inserted.LoadRunId
            VALUES (@ExtractJobId, @TargetServerId, @TargetDatabase, @TargetSchema, @TargetTable, 'Running', @watermarkFrom, @HostName, @triggeredBy);
            """, new { job.ExtractJobId, job.TargetServerId, job.TargetDatabase, job.TargetSchema, job.TargetTable, watermarkFrom, HostName, triggeredBy });
    }

    /// <summary>Marks the LoadRun as succeeded and stores the new watermark (if any) in the same transaction.</summary>
    public async Task CompleteLoadRunAsync(long loadRunId, int jobId, long rows, string? wmType, string? wmValue, MergeCounts? merge = null)
    {
        await using var c = Conn();
        await c.OpenAsync();
        await using var tx = await c.BeginTransactionAsync();
        await c.ExecuteAsync("""
            UPDATE etl.LoadRun SET Status = 'Succeeded', FinishedAtUtc = SYSUTCDATETIME(), RowsLoaded = @rows, WatermarkTo = @wmValue,
                   RowsInserted = @ins, RowsUpdated = @upd, RowsDeleted = @del
            WHERE LoadRunId = @loadRunId;
            """, new { loadRunId, rows, wmValue, ins = merge?.Inserted, upd = merge?.Updated, del = merge?.Deleted }, tx);

        if (wmValue is not null)
            await c.ExecuteAsync("""
                UPDATE etl.ExtractState SET WatermarkType = @wmType, WatermarkValue = @wmValue, UpdatedAtUtc = SYSUTCDATETIME()
                WHERE ExtractJobId = @jobId;
                IF @@ROWCOUNT = 0
                    INSERT etl.ExtractState (ExtractJobId, WatermarkType, WatermarkValue) VALUES (@jobId, @wmType, @wmValue);
                """, new { jobId, wmType, wmValue }, tx);
        await tx.CommitAsync();
    }

    public async Task FailLoadRunAsync(long loadRunId, string status, string? error)
    {
        await using var c = Conn();
        await c.ExecuteAsync("""
            UPDATE etl.LoadRun SET Status = @status, FinishedAtUtc = SYSUTCDATETIME(), ErrorMessage = @error WHERE LoadRunId = @loadRunId;
            """, new { loadRunId, status, error });
    }

    public async Task<string?> GetLoadStatusAsync(long loadRunId)
    {
        await using var c = Conn();
        return await c.ExecuteScalarAsync<string?>("SELECT Status FROM etl.LoadRun WHERE LoadRunId = @loadRunId", new { loadRunId });
    }

    /// <summary>Sets Abandoned only if still Running/CleanupPending (never overwrites a finished load).</summary>
    public async Task<bool> MarkLoadAbandonedAsync(long loadRunId, string message)
    {
        await using var c = Conn();
        return await c.ExecuteAsync("""
            UPDATE etl.LoadRun SET Status = 'Abandoned', FinishedAtUtc = SYSUTCDATETIME(), ErrorMessage = CONCAT(ErrorMessage, CHAR(10), @message)
            WHERE LoadRunId = @loadRunId AND Status IN ('Running', 'CleanupPending');
            """, new { loadRunId, message }) > 0;
    }

    public async Task MarkExportAbandonedAsync(long runId)
    {
        await using var c = Conn();
        await c.ExecuteAsync("""
            UPDATE etl.ExportRun SET Status = 'Abandoned', FinishedAtUtc = SYSUTCDATETIME(), ErrorMessage = N'Process was interrupted.'
            WHERE ExportRunId = @runId AND Status = 'Running';
            """, new { runId });
    }

    public async Task<IReadOnlyList<RunningLoad>> GetRunningLoadsAsync()
    {
        await using var c = Conn();
        return (await c.QueryAsync<RunningLoad>("""
            SELECT LoadRunId, ExtractJobId, TargetServerId, TargetDatabase, TargetSchema, TargetTable
            FROM etl.LoadRun WHERE Status IN ('Running', 'CleanupPending')
            """)).AsList();
    }

    // ================================================================ export jobs

    public async Task<IReadOnlyList<ExportJobDef>> GetExportJobsAsync()
    {
        await using var c = Conn();
        return (await c.QueryAsync<ExportJobDef>(ExportSelect + " ORDER BY e.Name")).AsList();
    }

    public async Task<ExportJobDef?> GetExportJobAsync(int id)
    {
        await using var c = Conn();
        return await c.QuerySingleOrDefaultAsync<ExportJobDef>(ExportSelect + " WHERE e.ExportJobId = @id", new { id });
    }

    public async Task<int> SaveExportJobAsync(ExportJobDef e, string user)
    {
        await using var c = Conn();
        e.ModifiedBy = user;
        bool isNew = e.ExportJobId == 0;
        if (isNew)
        {
            e.CreatedBy = user;
            e.ExportJobId = await c.ExecuteScalarAsync<int>("""
                INSERT etl.ExportJob (Name, ExportMode, SourceServerId, SourceDatabase, SourceSchema, SourceTable, SourceQuery, ColumnList,
                    IncludeTechnicalColumns, CronExpression, TimeZoneId, Bucket, ObjectNameTemplate, Delimiter, QuoteMode, NullValue,
                    LineEnding, Utf8Bom, Gzip, MaxRowsPerFile, UploadWhenEmpty, WriteManifest, CommandTimeoutSec, NotifyEmails, TriggerMode, IsActive, CreatedBy, ModifiedBy)
                OUTPUT inserted.ExportJobId
                VALUES (@Name, @ExportMode, @SourceServerId, @SourceDatabase, @SourceSchema, @SourceTable, @SourceQuery, @ColumnList,
                    @IncludeTechnicalColumns, @CronExpression, @TimeZoneId, @Bucket, @ObjectNameTemplate, @Delimiter, @QuoteMode, @NullValue,
                    @LineEnding, @Utf8Bom, @Gzip, @MaxRowsPerFile, @UploadWhenEmpty, @WriteManifest, @CommandTimeoutSec, @NotifyEmails, @TriggerMode, @IsActive, @CreatedBy, @ModifiedBy);
                """, e);
        }
        else
        {
            await c.ExecuteAsync("""
                UPDATE etl.ExportJob SET Name = @Name, ExportMode = @ExportMode, SourceServerId = @SourceServerId, SourceDatabase = @SourceDatabase,
                    SourceSchema = @SourceSchema, SourceTable = @SourceTable, SourceQuery = @SourceQuery, ColumnList = @ColumnList,
                    IncludeTechnicalColumns = @IncludeTechnicalColumns, CronExpression = @CronExpression, TimeZoneId = @TimeZoneId,
                    Bucket = @Bucket, ObjectNameTemplate = @ObjectNameTemplate, Delimiter = @Delimiter, QuoteMode = @QuoteMode,
                    NullValue = @NullValue, LineEnding = @LineEnding, Utf8Bom = @Utf8Bom, Gzip = @Gzip, MaxRowsPerFile = @MaxRowsPerFile,
                    UploadWhenEmpty = @UploadWhenEmpty, WriteManifest = @WriteManifest, CommandTimeoutSec = @CommandTimeoutSec,
                    NotifyEmails = @NotifyEmails, TriggerMode = @TriggerMode, IsActive = @IsActive, ModifiedBy = @ModifiedBy, ModifiedAtUtc = SYSUTCDATETIME()
                WHERE ExportJobId = @ExportJobId;
                """, e);
        }
        await AuditAsync(user, isNew ? "Create" : "Update", "ExportJob", e.ExportJobId, e);
        return e.ExportJobId;
    }

    public async Task DeleteExportJobAsync(int id, string user)
    {
        await using var c = Conn();
        await c.ExecuteAsync(DeleteDependenciesSql + "DELETE etl.ExportJob WHERE ExportJobId = @id;", new { id, kind = "Export" });
        await AuditAsync(user, "Delete", "ExportJob", id, null);
    }

    public async Task<long> GetLastExportedLoadIdAsync(int exportJobId)
    {
        await using var c = Conn();
        return await c.ExecuteScalarAsync<long?>(
            "SELECT LastExportedLoadId FROM etl.ExportState WHERE ExportJobId = @exportJobId", new { exportJobId }) ?? 0;
    }

    /// <summary>
    /// Safe upper bound: if a load is still writing to the table (Running/CleanupPending),
    /// one below it, otherwise the table's highest LoadRunId. Incomplete loads are never exported or skipped.
    /// </summary>
    public async Task<long> GetSafeMaxLoadIdAsync(int serverId, string database, string schema, string table)
    {
        await using var c = Conn();
        return await c.ExecuteScalarAsync<long>("""
            SELECT ISNULL(
                (SELECT MIN(LoadRunId) - 1 FROM etl.LoadRun
                 WHERE TargetServerId = @serverId AND TargetDatabase = @database AND TargetSchema = @schema AND TargetTable = @table
                   AND Status IN ('Running', 'CleanupPending')),
                (SELECT ISNULL(MAX(LoadRunId), 0) FROM etl.LoadRun
                 WHERE TargetServerId = @serverId AND TargetDatabase = @database AND TargetSchema = @schema AND TargetTable = @table));
            """, new { serverId, database, schema, table });
    }

    /// <summary>Distinct target tables of extract jobs in a database (AllTables export mode).</summary>
    public async Task<IReadOnlyList<CollectedTable>> GetCollectedTablesAsync(int serverId, string database, string? schemaFilter)
    {
        await using var c = Conn();
        return (await c.QueryAsync<CollectedTable>("""
            SELECT DISTINCT TargetSchema AS SchemaName, TargetTable AS TableName
            FROM etl.ExtractJob
            WHERE TargetServerId = @serverId AND TargetDatabase = @database
              AND (@schemaFilter IS NULL OR TargetSchema = @schemaFilter)
            ORDER BY TargetSchema, TargetTable;
            """, new { serverId, database, schemaFilter = string.IsNullOrWhiteSpace(schemaFilter) ? null : schemaFilter })).AsList();
    }

    public async Task<IReadOnlyList<ExportTableState>> GetExportTableStatesAsync(int exportJobId)
    {
        await using var c = Conn();
        return (await c.QueryAsync<ExportTableState>(
            "SELECT SchemaName, TableName, LastExportedLoadId, UpdatedAtUtc FROM etl.ExportTableState WHERE ExportJobId = @exportJobId",
            new { exportJobId })).AsList();
    }

    public async Task SaveExportTableStateAsync(int exportJobId, string schema, string table, long loadId)
    {
        await using var c = Conn();
        await c.ExecuteAsync("""
            UPDATE etl.ExportTableState SET LastExportedLoadId = @loadId, UpdatedAtUtc = SYSUTCDATETIME()
            WHERE ExportJobId = @exportJobId AND SchemaName = @schema AND TableName = @table;
            IF @@ROWCOUNT = 0
                INSERT etl.ExportTableState (ExportJobId, SchemaName, TableName, LastExportedLoadId) VALUES (@exportJobId, @schema, @table, @loadId);
            """, new { exportJobId, schema, table, loadId });
    }

    public async Task ResetExportStateAsync(int exportJobId, string user)
    {
        await using var c = Conn();
        await c.ExecuteAsync("""
            DELETE etl.ExportState WHERE ExportJobId = @exportJobId;
            DELETE etl.ExportTableState WHERE ExportJobId = @exportJobId;
            """, new { exportJobId });
        await AuditAsync(user, "ResetExportState", "ExportJob", exportJobId, null);
    }

    public async Task<long> StartExportRunAsync(int exportJobId, string? triggeredBy)
    {
        await using var c = Conn();
        return await c.ExecuteScalarAsync<long>("""
            INSERT etl.ExportRun (ExportJobId, Status, HostName, TriggeredBy) OUTPUT inserted.ExportRunId
            VALUES (@exportJobId, 'Running', @HostName, @triggeredBy);
            """, new { exportJobId, HostName, triggeredBy });
    }

    public async Task CompleteExportRunAsync(long runId, int exportJobId, string status, long rows, int files,
        long bytes, long? loadIdFrom, long? loadIdTo, string? objectNames, long? advanceStateTo, string? error = null)
    {
        await using var c = Conn();
        await c.OpenAsync();
        await using var tx = await c.BeginTransactionAsync();
        await c.ExecuteAsync("""
            UPDATE etl.ExportRun SET Status = @status, FinishedAtUtc = SYSUTCDATETIME(), RowsExported = @rows, FileCount = @files,
                   BytesUploaded = @bytes, LoadIdFrom = @loadIdFrom, LoadIdTo = @loadIdTo, ObjectNames = @objectNames,
                   ErrorMessage = @error
            WHERE ExportRunId = @runId;
            """, new { runId, status, rows, files, bytes, loadIdFrom, loadIdTo, objectNames, error }, tx);

        if (advanceStateTo is not null)
            await c.ExecuteAsync("""
                UPDATE etl.ExportState SET LastExportedLoadId = @v, UpdatedAtUtc = SYSUTCDATETIME() WHERE ExportJobId = @exportJobId;
                IF @@ROWCOUNT = 0 INSERT etl.ExportState (ExportJobId, LastExportedLoadId) VALUES (@exportJobId, @v);
                """, new { exportJobId, v = advanceStateTo.Value }, tx);
        await tx.CommitAsync();
    }

    public async Task FailExportRunAsync(long runId, string status, string? error)
    {
        await using var c = Conn();
        await c.ExecuteAsync("""
            UPDATE etl.ExportRun SET Status = @status, FinishedAtUtc = SYSUTCDATETIME(), ErrorMessage = @error WHERE ExportRunId = @runId;
            """, new { runId, status, error });
    }

    public async Task<IReadOnlyList<RunningExport>> GetRunningExportsAsync()
    {
        await using var c = Conn();
        return (await c.QueryAsync<RunningExport>("SELECT ExportRunId, ExportJobId FROM etl.ExportRun WHERE Status = 'Running'")).AsList();
    }

    // ================================================================ job dependencies

    /// <summary>Removes a job's dependency rows (as dependent and as upstream) and its trigger state; expects @kind and @id.</summary>
    private const string DeleteDependenciesSql = """
        DELETE etl.JobDependency WHERE (JobKind = @kind AND JobId = @id) OR (UpstreamKind = @kind AND UpstreamId = @id);
        DELETE etl.JobTrigger WHERE JobKind = @kind AND JobId = @id;

        """;

    public async Task<IReadOnlyList<JobDependencyRow>> GetJobDependenciesAsync()
    {
        await using var c = Conn();
        return (await c.QueryAsync<JobDependencyRow>("""
            SELECT d.JobKind, d.JobId, d.UpstreamKind, d.UpstreamId, d.CreatedAtUtc,
                   COALESCE(j.Name, e.Name, CONCAT(d.UpstreamKind, ' #', d.UpstreamId)) AS UpstreamName
            FROM etl.JobDependency d
            LEFT JOIN etl.ExtractJob j ON d.UpstreamKind = 'Extract' AND j.ExtractJobId = d.UpstreamId
            LEFT JOIN etl.ExportJob e ON d.UpstreamKind = 'Export' AND e.ExportJobId = d.UpstreamId
            ORDER BY d.JobKind, d.JobId, UpstreamName;
            """)).AsList();
    }

    /// <summary>
    /// Replaces the upstream jobs of a job. Rows that stay keep their CreatedAtUtc, so an unchanged
    /// dependency does not reset the "newer success" baseline; new rows only count successes from now on.
    /// </summary>
    public async Task SaveJobDependenciesAsync(string jobKind, int jobId, IReadOnlyCollection<JobRef> upstreams, string user)
    {
        await using var c = Conn();
        await c.OpenAsync();
        await using var tx = await c.BeginTransactionAsync();
        var existing = (await c.QueryAsync<(int Id, string Kind, int UpId)>(
            "SELECT JobDependencyId, UpstreamKind, UpstreamId FROM etl.JobDependency WITH (UPDLOCK, HOLDLOCK) WHERE JobKind = @jobKind AND JobId = @jobId",
            new { jobKind, jobId }, tx)).AsList();
        var wanted = upstreams.Distinct().ToList();

        foreach (var row in existing.Where(r => !wanted.Contains(new JobRef(r.Kind, r.UpId))))
            await c.ExecuteAsync("DELETE etl.JobDependency WHERE JobDependencyId = @Id", new { row.Id }, tx);
        foreach (var up in wanted.Where(u => !existing.Any(r => r.Kind == u.Kind && r.UpId == u.Id)))
            await c.ExecuteAsync("""
                INSERT etl.JobDependency (JobKind, JobId, UpstreamKind, UpstreamId, CreatedBy) VALUES (@jobKind, @jobId, @Kind, @Id, @user);
                """, new { jobKind, jobId, up.Kind, up.Id, user }, tx);
        if (wanted.Count == 0)
            await c.ExecuteAsync("DELETE etl.JobTrigger WHERE JobKind = @jobKind AND JobId = @jobId", new { jobKind, jobId }, tx);
        await tx.CommitAsync();

        if (wanted.Count != existing.Count || existing.Any(r => !wanted.Contains(new JobRef(r.Kind, r.UpId))))
            await AuditAsync(user, "SetDependencies", jobKind + "Job", jobId, new { Upstreams = wanted.Select(u => u.ToString()) });
    }

    /// <summary>Every dependency row with the upstream's latest success and the dependent's last dependency trigger.</summary>
    public async Task<IReadOnlyList<DependencyStateRow>> GetDependencyStatesAsync()
    {
        await using var c = Conn();
        return (await c.QueryAsync<DependencyStateRow>("""
            WITH ok AS (
                SELECT 'Extract' AS Kind, r.ExtractJobId AS JobId, MAX(r.FinishedAtUtc) AS LastOkUtc
                FROM etl.LoadRun r
                WHERE r.Status = 'Succeeded' AND r.ExtractJobId IN (SELECT UpstreamId FROM etl.JobDependency WHERE UpstreamKind = 'Extract')
                GROUP BY r.ExtractJobId
                UNION ALL
                -- an export with nothing new to send (Skipped) has finished its work too
                SELECT 'Export', r.ExportJobId, MAX(r.FinishedAtUtc)
                FROM etl.ExportRun r
                WHERE r.Status IN ('Succeeded', 'Skipped') AND r.ExportJobId IN (SELECT UpstreamId FROM etl.JobDependency WHERE UpstreamKind = 'Export')
                GROUP BY r.ExportJobId)
            SELECT d.JobKind, d.JobId, d.UpstreamKind, d.UpstreamId, d.CreatedAtUtc,
                   ok.LastOkUtc AS UpstreamLastSuccessUtc, t.LastTriggeredAtUtc
            FROM etl.JobDependency d
            LEFT JOIN ok ON ok.Kind = d.UpstreamKind AND ok.JobId = d.UpstreamId
            LEFT JOIN etl.JobTrigger t ON t.JobKind = d.JobKind AND t.JobId = d.JobId;
            """)).AsList();
    }

    /// <summary>Records that a dependency-triggered job was enqueued now (database clock, same as run timestamps).</summary>
    public async Task SetJobTriggeredAsync(JobRef job)
    {
        await using var c = Conn();
        await c.ExecuteAsync("""
            UPDATE etl.JobTrigger SET LastTriggeredAtUtc = SYSUTCDATETIME() WHERE JobKind = @Kind AND JobId = @Id;
            IF @@ROWCOUNT = 0 INSERT etl.JobTrigger (JobKind, JobId, LastTriggeredAtUtc) VALUES (@Kind, @Id, SYSUTCDATETIME());
            """, new { job.Kind, job.Id });
    }

    // ================================================================ data protection

    public async Task<IReadOnlyList<Privacy.MaskingRule>> GetMaskingRulesAsync()
    {
        await using var c = Conn();
        return (await c.QueryAsync<Privacy.MaskingRule>("SELECT * FROM etl.MaskingRule ORDER BY Name")).AsList();
    }

    public async Task<int> SaveMaskingRuleAsync(Privacy.MaskingRule r, string user)
    {
        await using var c = Conn();
        r.ModifiedBy = user;
        bool isNew = r.MaskingRuleId == 0;
        if (isNew)
            r.MaskingRuleId = await c.ExecuteScalarAsync<int>("""
                INSERT etl.MaskingRule (Name, ColumnPattern, ScopePattern, Method, KeepLast, AppliesTo, IsActive, ModifiedBy)
                OUTPUT inserted.MaskingRuleId
                VALUES (@Name, @ColumnPattern, @ScopePattern, @Method, @KeepLast, @AppliesTo, @IsActive, @ModifiedBy);
                """, r);
        else
            await c.ExecuteAsync("""
                UPDATE etl.MaskingRule SET Name = @Name, ColumnPattern = @ColumnPattern, ScopePattern = @ScopePattern, Method = @Method,
                       KeepLast = @KeepLast, AppliesTo = @AppliesTo, IsActive = @IsActive, ModifiedBy = @ModifiedBy, ModifiedAtUtc = SYSUTCDATETIME()
                WHERE MaskingRuleId = @MaskingRuleId;
                """, r);
        await AuditAsync(user, isNew ? "Create" : "Update", "MaskingRule", r.MaskingRuleId,
            new { r.Name, r.ColumnPattern, r.ScopePattern, Method = r.Method.ToString(), r.KeepLast, AppliesTo = r.AppliesTo.ToString(), r.IsActive });
        return r.MaskingRuleId;
    }

    public async Task DeleteMaskingRuleAsync(int id, string user)
    {
        await using var c = Conn();
        await c.ExecuteAsync("DELETE etl.MaskingRule WHERE MaskingRuleId = @id", new { id });
        await AuditAsync(user, "Delete", "MaskingRule", id, null);
    }

    // ================================================================ monitoring

    public async Task<IReadOnlyList<JobStatusRow>> GetJobStatusAsync()
    {
        await using var c = Conn();
        return (await c.QueryAsync<JobStatusRow>("SELECT * FROM etl.vJobStatus ORDER BY JobKind DESC, Name")).AsList();
    }

    public async Task<IReadOnlyList<RunRow>> GetRunsAsync(string? kind, int? jobId, string? status, int top = 200)
    {
        await using var c = Conn();
        return (await c.QueryAsync<RunRow>("""
            SELECT TOP (@top) * FROM (
                SELECT lr.LoadRunId AS RunId, 'Extract' AS Kind, lr.ExtractJobId AS JobId, ISNULL(j.Name, CONCAT('#', lr.ExtractJobId)) AS JobName,
                       lr.Status, lr.TriggeredBy, lr.StartedAtUtc, lr.FinishedAtUtc, lr.RowsLoaded AS [Rows],
                       CONCAT(lr.TargetDatabase, '.', lr.TargetSchema, '.', lr.TargetTable,
                              CASE WHEN lr.RowsInserted IS NOT NULL THEN CONCAT('  merge: +', lr.RowsInserted, ' ~', lr.RowsUpdated, ' -', lr.RowsDeleted) END,
                              CASE WHEN lr.WatermarkTo IS NOT NULL THEN CONCAT('  wm: ', lr.WatermarkFrom, ' → ', lr.WatermarkTo) END) AS Detail,
                       lr.ErrorMessage
                FROM etl.LoadRun lr LEFT JOIN etl.ExtractJob j ON j.ExtractJobId = lr.ExtractJobId
                WHERE (@kind IS NULL OR @kind = 'Extract')
                UNION ALL
                SELECT er.ExportRunId, 'Export', er.ExportJobId, ISNULL(e.Name, CONCAT('#', er.ExportJobId)),
                       er.Status, er.TriggeredBy, er.StartedAtUtc, er.FinishedAtUtc, er.RowsExported,
                       er.ObjectNames, er.ErrorMessage
                FROM etl.ExportRun er LEFT JOIN etl.ExportJob e ON e.ExportJobId = er.ExportJobId
                WHERE (@kind IS NULL OR @kind = 'Export')
            ) x
            WHERE (@jobId IS NULL OR x.JobId = @jobId) AND (@status IS NULL OR x.Status = @status)
            ORDER BY x.StartedAtUtc DESC;
            """, new { kind, jobId, status, top })).AsList();
    }

    public async Task<IReadOnlyList<AuditRow>> GetAuditAsync(int top = 300)
    {
        await using var c = Conn();
        return (await c.QueryAsync<AuditRow>("SELECT TOP (@top) * FROM etl.AuditLog ORDER BY AuditLogId DESC", new { top })).AsList();
    }

    public async Task AuditAsync(string user, string action, string entity, object? entityId, object? details)
    {
        await using var c = Conn();
        await c.ExecuteAsync("""
            INSERT etl.AuditLog (UserName, Action, Entity, EntityId, Details) VALUES (@user, @action, @entity, @id, @details);
            """, new { user, action, entity, id = entityId?.ToString(), details = details is null ? null : JsonSerializer.Serialize(details) });
    }

    // ================================================================ alerts

    public async Task<IReadOnlyList<Alerts.AlertChannelDef>> GetAlertChannelsAsync()
    {
        await using var c = Conn();
        return (await c.QueryAsync<Alerts.AlertChannelDef>("SELECT * FROM etl.AlertChannel ORDER BY Name")).AsList();
    }

    public async Task<int> SaveAlertChannelAsync(Alerts.AlertChannelDef ch, string user)
    {
        await using var c = Conn();
        ch.ModifiedBy = user;
        int id;
        if (ch.AlertChannelId == 0)
            id = await c.ExecuteScalarAsync<int>("""
                INSERT etl.AlertChannel (Name, ChannelType, Target, Statuses, JobKinds, JobNameFilter, IsActive, ModifiedBy)
                OUTPUT inserted.AlertChannelId
                VALUES (@Name, @ChannelType, @Target, @Statuses, @JobKinds, @JobNameFilter, @IsActive, @ModifiedBy);
                """, ch);
        else
        {
            await c.ExecuteAsync("""
                UPDATE etl.AlertChannel SET Name = @Name, ChannelType = @ChannelType, Target = @Target, Statuses = @Statuses, JobKinds = @JobKinds,
                       JobNameFilter = @JobNameFilter, IsActive = @IsActive, ModifiedBy = @ModifiedBy, ModifiedAtUtc = SYSUTCDATETIME()
                WHERE AlertChannelId = @AlertChannelId;
                """, ch);
            id = ch.AlertChannelId;
        }
        // the webhook URL is a secret: audited masked
        await AuditAsync(user, ch.AlertChannelId == 0 ? "Create" : "Update", "AlertChannel", id, new
        {
            ch.Name, ch.ChannelType, Target = Alerts.AlertFormat.MaskTarget(ch.ChannelType, ch.Target), ch.Statuses, ch.JobKinds, ch.JobNameFilter, ch.IsActive
        });
        return id;
    }

    public async Task DeleteAlertChannelAsync(int id, string user)
    {
        await using var c = Conn();
        await c.ExecuteAsync("DELETE etl.AlertChannel WHERE AlertChannelId = @id", new { id });
        await AuditAsync(user, "Delete", "AlertChannel", id, null);
    }

    /// <summary>Finished runs (not Running; exports also not Skipped) since a point in time, with the job's previous finished status.</summary>
    public async Task<IReadOnlyList<Alerts.FinishedRunRow>> GetFinishedRunsAsync(DateTime sinceUtc)
    {
        await using var c = Conn();
        return (await c.QueryAsync<Alerts.FinishedRunRow>("""
            WITH x AS (
                SELECT 'Extract' AS Kind, r.LoadRunId AS RunId, r.ExtractJobId AS JobId, r.Status, r.StartedAtUtc, r.FinishedAtUtc,
                       r.RowsLoaded AS [Rows], r.ErrorMessage, r.HostName, r.TriggeredBy,
                       LAG(r.Status) OVER (PARTITION BY r.ExtractJobId ORDER BY r.LoadRunId) AS PrevStatus
                FROM etl.LoadRun r WHERE r.Status <> 'Running'
                UNION ALL
                SELECT 'Export', r.ExportRunId, r.ExportJobId, r.Status, r.StartedAtUtc, r.FinishedAtUtc,
                       r.RowsExported, r.ErrorMessage, r.HostName, r.TriggeredBy,
                       LAG(r.Status) OVER (PARTITION BY r.ExportJobId ORDER BY r.ExportRunId)
                FROM etl.ExportRun r WHERE r.Status NOT IN ('Running', 'Skipped'))
            SELECT x.*, COALESCE(j.Name, e.Name, CONCAT(x.Kind, ' #', x.JobId)) AS JobName, COALESCE(j.NotifyEmails, e.NotifyEmails) AS NotifyEmails
            FROM x
            LEFT JOIN etl.ExtractJob j ON x.Kind = 'Extract' AND j.ExtractJobId = x.JobId
            LEFT JOIN etl.ExportJob e ON x.Kind = 'Export' AND e.ExportJobId = x.JobId
            WHERE COALESCE(x.FinishedAtUtc, x.StartedAtUtc) >= @sinceUtc
            ORDER BY COALESCE(x.FinishedAtUtc, x.StartedAtUtc);
            """, new { sinceUtc })).AsList();
    }

    /// <summary>Number of consecutive problem runs of a job ending with (and including) the given run.</summary>
    public async Task<int> GetConsecutiveProblemCountAsync(string kind, int jobId, long runId)
    {
        await using var c = Conn();
        var statuses = (await c.QueryAsync<string>(kind == "Extract"
            ? "SELECT TOP (100) Status FROM etl.LoadRun WHERE ExtractJobId = @jobId AND LoadRunId <= @runId AND Status <> 'Running' ORDER BY LoadRunId DESC"
            : "SELECT TOP (100) Status FROM etl.ExportRun WHERE ExportJobId = @jobId AND ExportRunId <= @runId AND Status NOT IN ('Running','Skipped') ORDER BY ExportRunId DESC",
            new { jobId, runId })).ToList();
        return statuses.TakeWhile(s => Alerts.AlertEvents.Problems.Contains(s)).Count();
    }

    /// <summary>Latest run start and whether a run is in progress, per job.</summary>
    public async Task<IReadOnlyList<(string Kind, int JobId, DateTime LastStartUtc, bool Running)>> GetLastRunStartsAsync()
    {
        await using var c = Conn();
        return (await c.QueryAsync<(string, int, DateTime, bool)>("""
            SELECT 'Extract', ExtractJobId, MAX(StartedAtUtc), CAST(MAX(CASE WHEN Status = 'Running' THEN 1 ELSE 0 END) AS bit) FROM etl.LoadRun GROUP BY ExtractJobId
            UNION ALL
            SELECT 'Export', ExportJobId, MAX(StartedAtUtc), CAST(MAX(CASE WHEN Status = 'Running' THEN 1 ELSE 0 END) AS bit) FROM etl.ExportRun GROUP BY ExportJobId;
            """)).AsList();
    }

    public async Task<IReadOnlyList<Alerts.AlertLogRow>> GetAlertLogSinceAsync(DateTime sinceUtc)
    {
        await using var c = Conn();
        return (await c.QueryAsync<Alerts.AlertLogRow>("SELECT * FROM etl.AlertLog WHERE CreatedAtUtc >= @sinceUtc", new { sinceUtc })).AsList();
    }

    public async Task<IReadOnlyList<Alerts.AlertLogRow>> GetRecentAlertLogAsync(int top = 100)
    {
        await using var c = Conn();
        return (await c.QueryAsync<Alerts.AlertLogRow>("SELECT TOP (@top) * FROM etl.AlertLog ORDER BY AlertLogId DESC", new { top })).AsList();
    }

    /// <summary>True if an alert for the same job, event and destination was sent since the given time (for another run).</summary>
    public async Task<bool> WasAlertSentSinceAsync(string jobKind, int jobId, string eventType, string channelKey, long exceptRunId, DateTime sinceUtc)
    {
        await using var c = Conn();
        return await c.ExecuteScalarAsync<int>("""
            SELECT COUNT(*) FROM etl.AlertLog
            WHERE JobKind = @jobKind AND JobId = @jobId AND EventType = @eventType AND ChannelKey = @channelKey
              AND Status = 'Sent' AND SentAtUtc >= @sinceUtc AND RunId <> @exceptRunId;
            """, new { jobKind, jobId, eventType, channelKey, exceptRunId, sinceUtc }) > 0;
    }

    /// <summary>Inserts or updates the delivery record of one event for one destination.</summary>
    public async Task SaveAlertLogAsync(Alerts.AlertLogRow row)
    {
        await using var c = Conn();
        await c.ExecuteAsync("""
            UPDATE etl.AlertLog SET Status = @Status, Attempts = @Attempts, LastError = @LastError, SentAtUtc = @SentAtUtc, Destination = @Destination
            WHERE EventType = @EventType AND JobKind = @JobKind AND JobId = @JobId AND RunId = @RunId AND ChannelKey = @ChannelKey;
            IF @@ROWCOUNT = 0
                INSERT etl.AlertLog (EventType, JobKind, JobId, JobName, RunId, ChannelKey, Destination, Status, Attempts, LastError, SentAtUtc)
                VALUES (@EventType, @JobKind, @JobId, @JobName, @RunId, @ChannelKey, @Destination, @Status, @Attempts, @LastError, @SentAtUtc);
            """, row);
    }

    // ================================================================ system settings

    /// <summary>Stored settings (name → value; a null value means explicitly "none").</summary>
    public async Task<IReadOnlyDictionary<string, string?>> GetSystemSettingsAsync()
    {
        await using var c = Conn();
        var rows = await c.QueryAsync<(string Name, string? Value)>("SELECT Name, Value FROM etl.SystemSetting");
        return rows.ToDictionary(r => r.Name, r => r.Value, StringComparer.OrdinalIgnoreCase);
    }

    public async Task SaveSystemSettingsAsync(IReadOnlyDictionary<string, string?> values, string user)
    {
        await using var c = Conn();
        await c.OpenAsync();
        await using var tx = await c.BeginTransactionAsync();
        foreach (var (name, value) in values)
            await c.ExecuteAsync("""
                UPDATE etl.SystemSetting SET Value = @value, ModifiedBy = @user, ModifiedAtUtc = SYSUTCDATETIME() WHERE Name = @name;
                IF @@ROWCOUNT = 0 INSERT etl.SystemSetting (Name, Value, ModifiedBy) VALUES (@name, @value, @user);
                """, new { name, value, user }, tx);
        await tx.CommitAsync();
        await AuditAsync(user, "Update", "SystemSetting", null, values);
    }

    /// <summary>Stores one setting whose value must not appear in the audit log (e.g. a password hash); the change itself is audited.</summary>
    public async Task SaveSecretSettingAsync(string name, string? value, string user, string auditEntity)
    {
        await using var c = Conn();
        await c.ExecuteAsync("""
            UPDATE etl.SystemSetting SET Value = @value, ModifiedBy = @user, ModifiedAtUtc = SYSUTCDATETIME() WHERE Name = @name;
            IF @@ROWCOUNT = 0 INSERT etl.SystemSetting (Name, Value, ModifiedBy) VALUES (@name, @value, @user);
            """, new { name, value, user });
        await AuditAsync(user, "Update", auditEntity, name, null);
    }

    // ================================================================ permission checks

    /// <summary>Replaces the latest check result of one process (role + queue + host) (older results are purged by retention).</summary>
    public async Task SavePermissionChecksAsync(string processRole, string? queue, string hostName, IReadOnlyList<Services.PermissionCheckRow> rows)
    {
        await using var c = Conn();
        await c.OpenAsync();
        await using var tx = await c.BeginTransactionAsync();
        await c.ExecuteAsync("""
            DELETE etl.PermissionCheck
            WHERE ProcessRole = @processRole AND HostName = @hostName AND ISNULL(Queue, '') = ISNULL(@queue, '');
            """, new { processRole, queue, hostName }, tx);
        if (rows.Count > 0)
            await c.ExecuteAsync("""
                INSERT etl.PermissionCheck (CheckedAtUtc, ProcessRole, Queue, HostName, ProcessAccount, Category, ServerName, DatabaseName, LoginName, Item, Status, Detail)
                VALUES (@CheckedAtUtc, @ProcessRole, @Queue, @HostName, @ProcessAccount, @Category, @ServerName, @DatabaseName, @LoginName, @Item, @Status, @Detail);
                """, rows, tx);
        await tx.CommitAsync();
    }

    public async Task<IReadOnlyList<Services.PermissionCheckRow>> GetPermissionChecksAsync()
    {
        await using var c = Conn();
        return (await c.QueryAsync<Services.PermissionCheckRow>(
            "SELECT * FROM etl.PermissionCheck ORDER BY ProcessRole DESC, HostName, Queue, PermissionCheckId")).AsList();
    }

    // ================================================================ housekeeping

    public async Task<long> PurgeRunLogsAsync(int runLogDays, int auditDays, int permissionCheckDays, int batchSize, CancellationToken ct)
    {
        await using var c = await factory.OpenMetadataAsync(ct);
        var now = DateTime.UtcNow;
        long n = 0;
        n += await SqlHelpers.DeleteInBatchesAsync(c, "etl", "LoadRun", "StartedAtUtc < @cutoff AND Status NOT IN ('Running', 'CleanupPending')",
            p => p.AddWithValue("@cutoff", now.AddDays(-runLogDays)), batchSize, ct);
        n += await SqlHelpers.DeleteInBatchesAsync(c, "etl", "ExportRun", "StartedAtUtc < @cutoff AND Status <> 'Running'",
            p => p.AddWithValue("@cutoff", now.AddDays(-runLogDays)), batchSize, ct);
        n += await SqlHelpers.DeleteInBatchesAsync(c, "etl", "AuditLog", "AtUtc < @cutoff",
            p => p.AddWithValue("@cutoff", now.AddDays(-auditDays)), batchSize, ct);
        n += await SqlHelpers.DeleteInBatchesAsync(c, "etl", "PermissionCheck", "CheckedAtUtc < @cutoff",
            p => p.AddWithValue("@cutoff", now.AddDays(-permissionCheckDays)), batchSize, ct);
        n += await SqlHelpers.DeleteInBatchesAsync(c, "etl", "AlertLog", "CreatedAtUtc < @cutoff",
            p => p.AddWithValue("@cutoff", now.AddDays(-runLogDays)), batchSize, ct);
        return n;
    }

    public static string Truncate(Exception ex, int max = 8000)
    {
        var s = ex.ToString();
        return s.Length <= max ? s : s[..max];
    }
}
