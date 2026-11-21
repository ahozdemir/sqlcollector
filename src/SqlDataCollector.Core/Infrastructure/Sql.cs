using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using SqlDataCollector.Models;

namespace SqlDataCollector.Infrastructure;

public static class SqlIdent
{
    public const string LoadIdColumn = "_LoadId";
    public const string LoadedAtColumn = "_LoadedAt";
    public const string ExtractJobIdColumn = "_ExtractJobId";
    public const string SourceServerColumn = "_SourceServer";

    public static readonly string[] TechnicalColumns =
        [LoadIdColumn, LoadedAtColumn, ExtractJobIdColumn, SourceServerColumn];

    public static bool IsTechnical(string column) =>
        TechnicalColumns.Contains(column, StringComparer.OrdinalIgnoreCase);

    public static string Quote(string name) => "[" + name.Replace("]", "]]") + "]";

    public static string Quote(string schema, string table) => Quote(schema) + "." + Quote(table);
}

public sealed record ConnectionDescription(string Server, string? Database, string Authentication);

public sealed class SqlConnectionFactory(IConfiguration configuration)
{
    /// <summary>Metadata database.</summary>
    public string MetadataConnectionString =>
        configuration.GetConnectionString("Metadata")
        ?? throw new InvalidOperationException("ConnectionStrings:Metadata is not configured.");

    public SqlConnection Metadata() => new(MetadataConnectionString);

    /// <summary>True when ConnectionStrings:Hangfire is set (shared/central Hangfire database).</summary>
    public bool IsSharedHangfire => configuration.GetConnectionString("Hangfire") is { Length: > 0 };

    /// <summary>Hangfire storage: ConnectionStrings:Hangfire, or the metadata database (embedded mode).</summary>
    public string HangfireConnectionString =>
        configuration.GetConnectionString("Hangfire") is { Length: > 0 } h ? h : MetadataConnectionString;

    /// <summary>Server, database and authentication of a connection string, for display. Never contains the password.</summary>
    public static ConnectionDescription Describe(string connectionString)
    {
        var b = new SqlConnectionStringBuilder(connectionString);
        var auth = b.IntegratedSecurity ? "Windows (Integrated Security)" : string.IsNullOrEmpty(b.UserID) ? b.Authentication.ToString() : $"SQL login {b.UserID}";
        return new ConnectionDescription(b.DataSource, string.IsNullOrEmpty(b.InitialCatalog) ? null : b.InitialCatalog, auth);
    }

    public async Task<SqlConnection> OpenMetadataAsync(CancellationToken ct)
    {
        var conn = Metadata();
        await conn.OpenAsync(ct);
        return conn;
    }

    /// <summary>
    /// Connection string for a defined SQL Server. Default: Integrated Security
    /// (the AD service account / gMSA the app runs as). No passwords are stored.
    /// Exception: SqlCredentialOverrides:&lt;ServerName&gt; (non-domain server / test environment).
    /// </summary>
    public string BuildConnectionString(SqlServerDef server, string? database, bool forWrite)
    {
        var b = new SqlConnectionStringBuilder
        {
            DataSource = server.ServerAddress,
            IntegratedSecurity = true,
            Encrypt = server.EncryptMode switch
            {
                "Optional" => SqlConnectionEncryptOption.Optional,
                "Strict" => SqlConnectionEncryptOption.Strict,
                _ => SqlConnectionEncryptOption.Mandatory
            },
            TrustServerCertificate = server.TrustServerCertificate,
            ApplicationIntent = server.ReadOnlyIntent && !forWrite ? ApplicationIntent.ReadOnly : ApplicationIntent.ReadWrite,
            MultiSubnetFailover = server.MultiSubnetFailover,
            ConnectTimeout = server.ConnectTimeoutSec > 0 ? server.ConnectTimeoutSec : 30,
            ApplicationName = "SqlDataCollector"
        };
        if (!string.IsNullOrWhiteSpace(database)) b.InitialCatalog = database;

        var overrideCs = configuration[$"SqlCredentialOverrides:{server.Name}"];
        if (!string.IsNullOrWhiteSpace(overrideCs))
        {
            var o = new SqlConnectionStringBuilder(overrideCs);
            b.IntegratedSecurity = false;
            b.UserID = o.UserID;
            b.Password = o.Password;
        }
        return b.ConnectionString;
    }

    /// <summary>Collector:ExportReadAsUser; null when disabled (empty).</summary>
    public string? ExportReadAsUser =>
        (configuration.GetSection(Options.CollectorOptions.Section).Get<Options.CollectorOptions>() ?? new()).ExportReadAsUser is { Length: > 0 } u ? u.Trim() : null;

    /// <summary>
    /// Connection for export reads (and export previews): switches to the read-only database user Collector:ExportReadAsUser
    /// with EXECUTE AS USER ... WITH COOKIE. Free SQL on that connection cannot REVERT (no cookie) and only has the user's rights.
    /// Not pooled: the impersonated session never returns to the pool.
    /// </summary>
    public async Task<ReadAsConnection> OpenExportReadAsync(SqlServerDef server, string database, bool forWrite, CancellationToken ct)
    {
        var user = ExportReadAsUser;
        if (user is null) return new ReadAsConnection(await OpenAsync(server, database, forWrite, ct), null, null);

        var csb = new SqlConnectionStringBuilder(BuildConnectionString(server, database, forWrite)) { Pooling = false };
        var conn = new SqlConnection(csb.ConnectionString);
        try
        {
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            // the user name comes from configuration, not from users: an escaped literal (EXECUTE AS takes no expressions)
            cmd.CommandText = $"DECLARE @cookie varbinary(8000); EXECUTE AS USER = N'{user.Replace("'", "''")}' WITH COOKIE INTO @cookie; SELECT @cookie;";
            var cookie = (byte[])(await cmd.ExecuteScalarAsync(ct))!;
            return new ReadAsConnection(conn, user, cookie);
        }
        catch (SqlException ex)
        {
            await conn.DisposeAsync();
            throw new InvalidOperationException(
                $"Export reads run as the database user '{user}' (Collector:ExportReadAsUser), but the session could not switch to it in [{database}] on {server.Name}: {ex.Message} " +
                $"The DBA creates it with: CREATE USER [{user}] WITHOUT LOGIN; ALTER ROLE db_datareader ADD MEMBER [{user}]; " +
                $"GRANT IMPERSONATE ON USER::[{user}] TO <Web and Worker service accounts>;", ex);
        }
        catch
        {
            await conn.DisposeAsync();
            throw;
        }
    }

    public async Task<SqlConnection> OpenAsync(SqlServerDef server, string? database, bool forWrite, CancellationToken ct)
    {
        var conn = new SqlConnection(BuildConnectionString(server, database, forWrite));
        try
        {
            await conn.OpenAsync(ct);
            return conn;
        }
        catch
        {
            await conn.DisposeAsync();
            throw;
        }
    }
}

/// <summary>A connection that may be impersonating a read-only database user; reverts (best effort) and closes on dispose.</summary>
public sealed class ReadAsConnection(SqlConnection connection, string? readAsUser, byte[]? cookie) : IAsyncDisposable
{
    public SqlConnection Connection { get; } = connection;

    /// <summary>The impersonated database user, or null when reads run with the service account's own rights.</summary>
    public string? ReadAsUser { get; } = readAsUser;

    public async ValueTask DisposeAsync()
    {
        if (cookie is not null)
        {
            try
            {
                await using var cmd = Connection.CreateCommand();
                cmd.CommandText = "REVERT WITH COOKIE = @cookie;";
                cmd.Parameters.Add(new SqlParameter("@cookie", System.Data.SqlDbType.VarBinary, 8000) { Value = cookie });
                await cmd.ExecuteNonQueryAsync();
            }
            catch
            {
                // non-pooled connection: closing it ends the impersonation anyway
            }
        }
        await Connection.DisposeAsync();
    }
}

/// <summary>
/// Session-owned distributed lock based on sp_getapplock. Prevents the same job from running
/// concurrently on several Hangfire servers/workers.
/// If the process crashes, the connection closes and SQL Server releases the lock.
/// </summary>
public sealed class AppLock : IAsyncDisposable
{
    private readonly SqlConnection _connection;
    private readonly string _resource;

    private AppLock(SqlConnection connection, string resource)
    {
        _connection = connection;
        _resource = resource;
    }

    public static async Task<AppLock?> TryAcquireAsync(string connectionString, string resource, int timeoutMs, CancellationToken ct)
    {
        // Non-pooled connection: the lock lives as long as the connection and never leaks through the pool.
        var csb = new SqlConnectionStringBuilder(connectionString) { Pooling = false, ApplicationName = "SqlDataCollector-Lock" };
        var conn = new SqlConnection(csb.ConnectionString);
        try
        {
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                DECLARE @r int;
                EXEC @r = sp_getapplock @Resource = @res, @LockMode = 'Exclusive', @LockOwner = 'Session', @LockTimeout = @t;
                SELECT @r;
                """;
            cmd.Parameters.AddWithValue("@res", resource);
            cmd.Parameters.AddWithValue("@t", timeoutMs);
            var result = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
            if (result >= 0) return new AppLock(conn, resource);
        }
        catch
        {
            await conn.DisposeAsync();
            throw;
        }

        await conn.DisposeAsync();
        return null;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await using var cmd = _connection.CreateCommand();
            cmd.CommandText = "EXEC sp_releaseapplock @Resource = @res, @LockOwner = 'Session';";
            cmd.Parameters.AddWithValue("@res", _resource);
            await cmd.ExecuteNonQueryAsync();
        }
        catch
        {
            // the lock is released anyway when the connection closes
        }
        finally
        {
            await _connection.DisposeAsync();
        }
    }
}

public static class SqlHelpers
{
    /// <summary>
    /// Deletes in small batches (avoids log growth and long locks).
    /// whereClause parameters are supplied via the callback.
    /// </summary>
    public static async Task<long> DeleteInBatchesAsync(
        SqlConnection conn, string schema, string table, string whereClause,
        Action<SqlParameterCollection> addParameters, int batchSize, CancellationToken ct)
    {
        long total = 0;
        var sql = $"DELETE TOP (@__batch) FROM {SqlIdent.Quote(schema, table)} WHERE {whereClause};";
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.CommandTimeout = 0;
            cmd.Parameters.AddWithValue("@__batch", batchSize);
            addParameters(cmd.Parameters);
            var affected = await cmd.ExecuteNonQueryAsync(ct);
            total += affected;
            if (affected < batchSize) return total;
        }
    }

    /// <summary>
    /// True if the table exists AND has the _LoadId column, i.e. rows of a load can be identified.
    /// A pre-existing table that failed the Strict check has no _LoadId: nothing was written, nothing to clean.
    /// </summary>
    public static async Task<bool> HasLoadIdColumnAsync(SqlConnection conn, string schema, string table, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT CASE WHEN COL_LENGTH(@n, @c) IS NULL THEN 0 ELSE 1 END;";
        cmd.Parameters.AddWithValue("@n", SqlIdent.Quote(schema, table));
        cmd.Parameters.AddWithValue("@c", SqlIdent.LoadIdColumn);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct)) == 1;
    }

    public static async Task<bool> TableExistsAsync(SqlConnection conn, string schema, string table, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT CASE WHEN OBJECT_ID(@n, N'U') IS NULL THEN 0 ELSE 1 END;";
        cmd.Parameters.AddWithValue("@n", SqlIdent.Quote(schema, table));
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct)) == 1;
    }
}

public static class TimeZones
{
    public static TimeZoneInfo Resolve(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return TimeZoneInfo.Utc;
        if (TimeZoneInfo.TryFindSystemTimeZoneById(id, out var tz)) return tz;
        throw new InvalidOperationException($"Time zone not found: '{id}'. E.g. 'Europe/Istanbul' or 'Turkey Standard Time'.");
    }
}

public static partial class SchemaInstaller
{
    [GeneratedRegex(@"^\s*GO\s*;?\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex GoSeparator();

    public static async Task InstallAsync(SqlConnectionFactory factory, ILogger logger, CancellationToken ct)
    {
        var asm = Assembly.GetExecutingAssembly();
        var resourceName = asm.GetManifestResourceNames().Single(n => n.EndsWith("001_schema.sql", StringComparison.OrdinalIgnoreCase));
        await using var stream = asm.GetManifestResourceStream(resourceName)!;
        using var reader = new StreamReader(stream);
        var script = await reader.ReadToEndAsync(ct);

        await using var conn = await factory.OpenMetadataAsync(ct);
        // Avoid a schema-install race when Web and Worker start at the same time
        await using (var lockCmd = conn.CreateCommand())
        {
            lockCmd.CommandText = "EXEC sp_getapplock @Resource = 'etl:schema', @LockMode = 'Exclusive', @LockOwner = 'Session', @LockTimeout = 120000;";
            await lockCmd.ExecuteNonQueryAsync(ct);
        }
        foreach (var batch in GoSeparator().Split(script).Where(b => !string.IsNullOrWhiteSpace(b)))
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = batch;
            cmd.CommandTimeout = 300;
            await cmd.ExecuteNonQueryAsync(ct);
        }
        logger.LogInformation("etl schema verified/created.");
    }
}
