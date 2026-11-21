using Hangfire;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using SqlDataCollector.Infrastructure;
using SqlDataCollector.Models;
using SqlDataCollector.Options;

namespace SqlDataCollector.Services;

public sealed class PermissionCheckRow
{
    public long PermissionCheckId { get; set; }
    public DateTime CheckedAtUtc { get; set; }
    public string ProcessRole { get; set; } = "";
    public string? Queue { get; set; }
    public string HostName { get; set; } = "";
    public string ProcessAccount { get; set; } = "";
    public string Category { get; set; } = "";
    public string ServerName { get; set; } = "";
    public string? DatabaseName { get; set; }
    public string? LoginName { get; set; }
    public string Item { get; set; } = "";
    public string Status { get; set; } = "";
    public string? Detail { get; set; }
}

/// <summary>
/// Checks the permissions of the account THIS process runs as (HAS_PERMS_BY_NAME, read-only: no data is read or written)
/// against the minimum of sql/01_permissions_template.sql, and reports permissions that must not be granted.
/// The Web process checks itself directly; Worker accounts are checked by PermissionCheckRunner jobs.
/// </summary>
public sealed class PermissionChecker(
    SqlConnectionFactory factory,
    MetadataRepository repo,
    IOptions<CollectorOptions> options,
    IOptions<Alerts.AlertOptions> alertOptions,
    ILogger<PermissionChecker> logger)
{

    public CheckContext WebContext() => Context(CheckContext.Web, null);

    public CheckContext WorkerContext(string queue) => Context(CheckContext.Worker, queue);

    private CheckContext Context(string role, string? queue)
    {
        var o = options.Value;
        return new CheckContext(role, queue, WebOnly: o.IsWeb && !o.IsWorker, o.AutoCreateSchema, o.HangfirePrepareSchema,
            factory.IsSharedHangfire, o.HangfireSchema, factory.ExportReadAsUser);
    }

    public static string ProcessAccount => $"{Environment.UserDomainName}\\{Environment.UserName}";

    public async Task<IReadOnlyList<PermissionCheckRow>> CheckAsync(CheckContext ctx, CancellationToken ct)
    {
        var rows = new List<PermissionCheckRow>();

        var meta = SqlConnectionFactory.Describe(factory.MetadataConnectionString);
        await CheckDatabaseAsync(rows, ctx, CheckCategory.Metadata, meta.Server, meta.Database,
            () => OpenAsync(factory.MetadataConnectionString, ct), _ => Task.FromResult(PermissionRules.Metadata(ctx)), null, ct);

        var hf = SqlConnectionFactory.Describe(factory.HangfireConnectionString);
        await CheckDatabaseAsync(rows, ctx, CheckCategory.Hangfire, hf.Server, hf.Database,
            () => OpenAsync(factory.HangfireConnectionString, ct), _ => Task.FromResult(PermissionRules.Hangfire(ctx)),
            conn => HangfireSchemaRowAsync(conn, ctx, ct), ct);

        // alerts are dispatched by the Web process: its network must reach SMTP and the webhooks
        if (ctx.IsWeb) await CheckAlertDestinationsAsync(rows, ctx, ct);

        if (!ctx.WritesTarget && !ctx.ReadsTarget && !ctx.ReadsSources) return rows;

        var servers = (await repo.GetServersAsync()).Where(s => s.IsActive).ToList();
        var extracts = (await repo.GetExtractJobsAsync()).Where(j => j.IsActive).ToList();
        var exports = (await repo.GetExportJobsAsync()).Where(e => e.IsActive).ToList();

        if (ctx.WritesTarget || ctx.ReadsTarget)
            foreach (var server in servers.Where(s => s.IsTarget))
            {
                var dbs = extracts.Where(j => j.TargetServerId == server.SqlServerId).Select(j => j.TargetDatabase)
                    .Concat(exports.Where(e => e.SourceServerId == server.SqlServerId).Select(e => e.SourceDatabase))
                    .Distinct(StringComparer.OrdinalIgnoreCase).Order().ToList();
                if (dbs.Count == 0) { await ConnectionOnlyAsync(rows, ctx, CheckCategory.Target, server, ct); continue; }
                foreach (var db in dbs)
                {
                    var jobs = extracts.Where(j => j.TargetServerId == server.SqlServerId && j.TargetDatabase.Equals(db, StringComparison.OrdinalIgnoreCase)).ToList();
                    var schemaNames = jobs.Select(j => j.TargetSchema)
                        .Concat(exports.Where(e => e.SourceServerId == server.SqlServerId && e.SourceDatabase.Equals(db, StringComparison.OrdinalIgnoreCase) && e.SourceSchema is not null)
                            .Select(e => e.SourceSchema!))
                        .Distinct(StringComparer.OrdinalIgnoreCase).Order().ToList();
                    var partitionDaily = jobs.Any(j => j.PartitionDaily);
                    await CheckDatabaseAsync(rows, ctx, CheckCategory.Target, server.Name, db,
                        () => factory.OpenAsync(server, db, forWrite: ctx.WritesTarget, ct),
                        async conn => PermissionRules.Target(ctx, await SchemasAsync(conn, schemaNames, ct), partitionDaily), null, ct);
                    if (ctx.ReadsTarget && exports.Any(e => e.SourceServerId == server.SqlServerId && e.SourceDatabase.Equals(db, StringComparison.OrdinalIgnoreCase)))
                        await CheckExportReaderAsync(rows, ctx, server, db, schemaNames, ct);
                }
            }

        if (ctx.ReadsSources)
            foreach (var server in servers.Where(s => s.IsSource))
            {
                var byDb = extracts.Where(j => j.SourceServerId == server.SqlServerId)
                    .GroupBy(j => j.SourceDatabase, StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key).ToList();
                if (byDb.Count == 0) { await ConnectionOnlyAsync(rows, ctx, CheckCategory.Source, server, ct); continue; }
                foreach (var g in byDb)
                {
                    var objects = g.Select(PermissionRules.SourceObjectOf).Distinct().ToList();
                    await CheckDatabaseAsync(rows, ctx, CheckCategory.Source, server.Name, g.Key,
                        () => factory.OpenAsync(server, g.Key, forWrite: false, ct),
                        _ => Task.FromResult(PermissionRules.Source(ctx, objects)), null, ct);
                }
            }
        return rows;
    }

    /// <summary>Runs a check and stores the result as the latest for this process (role + queue + host).</summary>
    public async Task<IReadOnlyList<PermissionCheckRow>> CheckAndSaveAsync(CheckContext ctx, CancellationToken ct)
    {
        var rows = await CheckAsync(ctx, ct);
        await repo.SavePermissionChecksAsync(ctx.ProcessRole, ctx.Queue, Environment.MachineName, rows);
        var problems = rows.Where(r => r.Status != CheckStatus.Ok).ToList();
        foreach (var r in problems)
            logger.LogWarning("Permission check {Role}/{Queue}: {Category} {Server}/{Database}: {Item} – {Status}. {Detail}",
                r.ProcessRole, r.Queue ?? "-", r.Category, r.ServerName, r.DatabaseName ?? "-", r.Item, r.Status, r.Detail);
        logger.LogInformation("Permission check {Role}/{Queue}: {Total} item(s), {Problems} problem(s).",
            ctx.ProcessRole, ctx.Queue ?? "-", rows.Count, problems.Count);
        return rows;
    }

    // ------------------------------------------------------------------ helpers

    private static async Task<SqlConnection> OpenAsync(string connectionString, CancellationToken ct)
    {
        var conn = new SqlConnection(connectionString);
        try { await conn.OpenAsync(ct); return conn; }
        catch { await conn.DisposeAsync(); throw; }
    }

    private PermissionCheckRow Row(CheckContext ctx, string category, string server, string? database, string? login, string item, string status, string? detail) => new()
    {
        CheckedAtUtc = DateTime.UtcNow, ProcessRole = ctx.ProcessRole, Queue = ctx.Queue, HostName = Environment.MachineName,
        ProcessAccount = ProcessAccount, Category = category, ServerName = server, DatabaseName = database, LoginName = login,
        Item = item, Status = status, Detail = detail
    };

    private async Task CheckDatabaseAsync(
        List<PermissionCheckRow> rows, CheckContext ctx, string category, string serverName, string? database,
        Func<Task<SqlConnection>> open, Func<SqlConnection, Task<IReadOnlyList<ProbeGroup>>> rules,
        Func<SqlConnection, Task<(string Item, string Status, string? Detail)?>>? extra, CancellationToken ct)
    {
        try
        {
            await using var conn = await open();
            var groups = await rules(conn);
            if (groups.Count == 0 && extra is null) return;
            var probes = groups.SelectMany(g => g.Probes).Distinct().ToList();
            var (login, sysadmin, dbOwner, results) = await EvaluateAsync(conn, probes, ct);
            if (extra is not null && await extra(conn) is { } x)
                rows.Add(Row(ctx, category, serverName, database, login, x.Item, x.Status, x.Detail));
            foreach (var g in groups)
            {
                var (status, detail) = PermissionRules.Evaluate(g, g.Probes.Select(p => results[p]).ToList(), sysadmin, dbOwner);
                rows.Add(Row(ctx, category, serverName, database, login, g.Item, status, detail));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            rows.Add(Row(ctx, category, serverName, database, null, "Connect", CheckStatus.Error, ex.Message));
        }
    }

    /// <summary>
    /// Export reads switch to Collector:ExportReadAsUser: verifies the switch works (user exists, IMPERSONATE granted)
    /// using the same code path as the export, then evaluates that user's own rights (read: yes, write/DDL: no).
    /// </summary>
    private async Task CheckExportReaderAsync(List<PermissionCheckRow> rows, CheckContext ctx, SqlServerDef server, string db,
        IReadOnlyList<string> schemaNames, CancellationToken ct)
    {
        const string item = "Export reads run as a read-only database user";
        if (ctx.ExportReadAsUser is null)
        {
            rows.Add(Row(ctx, CheckCategory.Target, server.Name, db, null, item, CheckStatus.Warning,
                "Collector:ExportReadAsUser is empty: export SQL written by Editors runs with the service account's own rights (write/DDL on the target)."));
            return;
        }
        try
        {
            await using var session = await factory.OpenExportReadAsync(server, db, forWrite: false, ct);
            var groups = PermissionRules.ExportReader(ctx.ExportReadAsUser, await SchemasAsync(session.Connection, schemaNames, ct));
            var probes = groups.SelectMany(g => g.Probes).Distinct().ToList();
            var (_, _, dbOwner, results) = await EvaluateAsync(session.Connection, probes, ct);
            rows.Add(Row(ctx, CheckCategory.Target, server.Name, db, ctx.ExportReadAsUser, item, CheckStatus.Ok,
                $"EXECUTE AS USER '{ctx.ExportReadAsUser}' works."));
            foreach (var g in groups)
            {
                var (status, detail) = PermissionRules.Evaluate(g, g.Probes.Select(p => results[p]).ToList(), isDbOwner: dbOwner);
                rows.Add(Row(ctx, CheckCategory.Target, server.Name, db, ctx.ExportReadAsUser, g.Item, status, detail));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            rows.Add(Row(ctx, CheckCategory.Target, server.Name, db, ctx.ExportReadAsUser, item, CheckStatus.Missing, ex.Message));
        }
    }

    /// <summary>TCP reachability of the SMTP server and of every active webhook host (nothing is sent).</summary>
    private async Task CheckAlertDestinationsAsync(List<PermissionCheckRow> rows, CheckContext ctx, CancellationToken ct)
    {
        var channels = (await repo.GetAlertChannelsAsync()).Where(c => c.IsActive).ToList();
        var jobMails = (await repo.GetExtractJobsAsync()).Any(j => j.IsActive && !string.IsNullOrWhiteSpace(j.NotifyEmails))
                       || (await repo.GetExportJobsAsync()).Any(e => e.IsActive && !string.IsNullOrWhiteSpace(e.NotifyEmails));
        if (channels.Any(c => c.ChannelType == Alerts.AlertChannelTypes.Email) || jobMails)
        {
            var smtp = alertOptions.Value.Smtp;
            if (string.IsNullOrWhiteSpace(smtp.Host))
                rows.Add(Row(ctx, CheckCategory.Alerts, "(SMTP)", null, null, "SMTP server reachable", CheckStatus.Error,
                    "E-mail alerts are configured but Alerts:Smtp:Host is empty."));
            else
                rows.Add(await TcpRowAsync(ctx, smtp.Host, smtp.Port, "SMTP server reachable", ct));
        }
        foreach (var c in channels.Where(c => c.ChannelType != Alerts.AlertChannelTypes.Email))
        {
            if (!Uri.TryCreate(c.Target, UriKind.Absolute, out var u))
                rows.Add(Row(ctx, CheckCategory.Alerts, c.Name, null, null, $"Alert channel '{c.Name}' reachable", CheckStatus.Error, "Invalid URL."));
            else
                rows.Add(await TcpRowAsync(ctx, u.Host, u.Port, $"Alert channel '{c.Name}' reachable", ct));
        }
    }

    private async Task<PermissionCheckRow> TcpRowAsync(CheckContext ctx, string host, int port, string item, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            using var tcp = new System.Net.Sockets.TcpClient();
            await tcp.ConnectAsync(host, port, cts.Token);
            return Row(ctx, CheckCategory.Alerts, $"{host}:{port}", null, null, item, CheckStatus.Ok, "TCP connection succeeded (nothing was sent).");
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            return Row(ctx, CheckCategory.Alerts, $"{host}:{port}", null, null, item, CheckStatus.Error,
                ex is OperationCanceledException ? "No TCP connection within 5 s (firewall/proxy?)." : ex.Message);
        }
    }

    private async Task ConnectionOnlyAsync(List<PermissionCheckRow> rows, CheckContext ctx, string category, SqlServerDef server, CancellationToken ct)
    {
        try
        {
            await using var conn = await factory.OpenAsync(server, "master", forWrite: false, ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT SUSER_SNAME();";
            var login = (string?)await cmd.ExecuteScalarAsync(ct);
            rows.Add(Row(ctx, category, server.Name, null, login, "Connect", CheckStatus.Ok,
                "No active job uses this server yet; database permissions are checked once jobs exist."));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            rows.Add(Row(ctx, category, server.Name, null, null, "Connect", CheckStatus.Error, ex.Message));
        }
    }

    private static async Task<(string? Login, bool Sysadmin, bool DbOwner, Dictionary<PermissionProbe, bool?> Results)> EvaluateAsync(
        SqlConnection conn, IReadOnlyList<PermissionProbe> probes, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        var cols = new List<string> { "SUSER_SNAME()", "IS_SRVROLEMEMBER('sysadmin')", "IS_ROLEMEMBER('db_owner')" };
        for (int i = 0; i < probes.Count; i++)
        {
            cols.Add($"HAS_PERMS_BY_NAME(COALESCE(@s{i}, DB_NAME()), @c{i}, @p{i})");
            cmd.Parameters.Add(new SqlParameter($"@s{i}", System.Data.SqlDbType.NVarChar, 512) { Value = (object?)probes[i].Securable ?? DBNull.Value });
            cmd.Parameters.Add(new SqlParameter($"@c{i}", System.Data.SqlDbType.NVarChar, 60) { Value = probes[i].SecurableClass });
            cmd.Parameters.Add(new SqlParameter($"@p{i}", System.Data.SqlDbType.NVarChar, 60) { Value = probes[i].Permission });
        }
        cmd.CommandText = "SELECT " + string.Join(", ", cols) + ";";
        await using var r = await cmd.ExecuteReaderAsync(ct);
        await r.ReadAsync(ct);
        var results = new Dictionary<PermissionProbe, bool?>();
        for (int i = 0; i < probes.Count; i++)
            results[probes[i]] = r.IsDBNull(i + 3) ? null : r.GetInt32(i + 3) == 1;
        return (r.IsDBNull(0) ? null : r.GetString(0), !r.IsDBNull(1) && r.GetInt32(1) == 1, !r.IsDBNull(2) && r.GetInt32(2) == 1, results);
    }

    private static async Task<IReadOnlyList<TargetSchema>> SchemasAsync(SqlConnection conn, IReadOnlyList<string> names, CancellationToken ct)
    {
        var list = new List<TargetSchema>();
        foreach (var n in names)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT CASE WHEN SCHEMA_ID(@s) IS NULL THEN 0 ELSE 1 END;";
            cmd.Parameters.AddWithValue("@s", n);
            list.Add(new TargetSchema(n, Convert.ToInt32(await cmd.ExecuteScalarAsync(ct)) == 1));
        }
        return list;
    }

    private static async Task<(string Item, string Status, string? Detail)?> HangfireSchemaRowAsync(SqlConnection conn, CheckContext ctx, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            DECLARE @v int = NULL;
            IF OBJECT_ID(@t, N'U') IS NOT NULL
            BEGIN
                DECLARE @sql nvarchar(600) = N'SELECT @v = MAX(Version) FROM ' + @t + N';';
                EXEC sp_executesql @sql, N'@v int OUTPUT', @v = @v OUTPUT;
            END
            SELECT @v;
            """;
        cmd.Parameters.AddWithValue("@t", SqlIdent.Quote(ctx.HangfireSchema, "Schema"));
        var v = await cmd.ExecuteScalarAsync(ct);
        return v is int version
            ? ("Hangfire schema installed", CheckStatus.Ok, $"Schema [{ctx.HangfireSchema}], version {version}.")
            : ("Hangfire schema installed", CheckStatus.Missing,
                $"Hangfire tables not found (or not visible) in schema [{ctx.HangfireSchema}]. Install them with 'SqlDataCollector hangfire-install' or HangfirePrepareSchema=true.");
    }
}

/// <summary>Hangfire job: checks the permissions of the Worker account that executes it, for one queue.</summary>
public sealed class PermissionCheckRunner(PermissionChecker checker)
{
    [JobDisplayName("Permission check ({0})")]
    [AutomaticRetry(Attempts = 0)]
    public Task RunAsync(string queue, CancellationToken ct) =>
        checker.CheckAndSaveAsync(checker.WorkerContext(queue), ct);
}

public sealed record WorkerCheckRequest(IReadOnlyList<string> Enqueued, IReadOnlyList<string> NoServer);

/// <summary>Enqueues a PermissionCheckRunner job on every queue a live Hangfire server listens to.</summary>
public sealed class PermissionCheckRequester(JobStorage storage, IBackgroundJobClient client)
{
    public WorkerCheckRequest RequestWorkerChecks()
    {
        var live = storage.GetMonitoringApi().Servers()
            .Where(s => s.Heartbeat is null || s.Heartbeat > DateTime.UtcNow.AddMinutes(-5)).ToList();
        var enqueued = new List<string>();
        var noServer = new List<string>();
        foreach (var queue in CollectorQueues.All)
        {
            if (!live.Any(s => s.Queues.Contains(queue, StringComparer.OrdinalIgnoreCase))) { noServer.Add(queue); continue; }
            var q = queue;
            client.Enqueue<PermissionCheckRunner>(q, r => r.RunAsync(q, CancellationToken.None));
            enqueued.Add(q);
        }
        return new WorkerCheckRequest(enqueued, noServer);
    }
}
