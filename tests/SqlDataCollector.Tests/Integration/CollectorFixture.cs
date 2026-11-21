using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SqlDataCollector.Export;
using SqlDataCollector.Extract;
using SqlDataCollector.Infrastructure;
using SqlDataCollector.Models;
using SqlDataCollector.Scheduling;
using Testcontainers.MsSql;

namespace SqlDataCollector.Tests.Integration;

/// <summary>
/// Starts SQL Server 2022 and a fake GCS server in Docker (Testcontainers), creates the metadata,
/// source (SrcDb) and target (DWH) databases, installs the etl schema and registers two servers.
/// One instance is shared by all integration tests (xUnit collection, tests run sequentially).
/// </summary>
public sealed partial class CollectorFixture : IAsyncLifetime
{
    public const string Bucket = "test-bucket";
    public const string SourceServerName = "SRC";
    public const string TargetServerName = "DWH";

    private MsSqlContainer _sql = null!;
    private IContainer _gcs = null!;
    private int _gcsPort;

    public ServiceProvider Services { get; private set; } = null!;
    public string WorkDirectory { get; } = Path.Combine(Path.GetTempPath(), "sdc-tests-" + Guid.NewGuid().ToString("N")[..8]);
    public int SourceServerId { get; private set; }
    public int TargetServerId { get; private set; }
    public string GcsBaseUrl => $"http://localhost:{_gcsPort}";
    public HttpClient Http { get; } = new();

    public async Task InitializeAsync()
    {
        _sql = new MsSqlBuilder(Environment.GetEnvironmentVariable("SDC_TEST_MSSQL_IMAGE") ?? "mcr.microsoft.com/mssql/server:2022-latest")
            .Build();

        // fake-gcs must know its public host:port for resumable upload URLs -> fixed, pre-allocated host port
        _gcsPort = FreeTcpPort();
        _gcs = new ContainerBuilder("fsouza/fake-gcs-server:latest")
            .WithPortBinding(_gcsPort, 4443)
            .WithCommand("-scheme", "http", "-port", "4443", "-public-host", $"localhost:{_gcsPort}")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(4443).ForPath("/storage/v1/b")))
            .Build();

        await Task.WhenAll(_sql.StartAsync(), _gcs.StartAsync());

        (await Http.PostAsJsonAsync($"{GcsBaseUrl}/storage/v1/b?project=test", new { name = Bucket })).EnsureSuccessStatusCode();

        var master = new SqlConnectionStringBuilder(_sql.GetConnectionString()) { TrustServerCertificate = true };
        await RunScriptAsync(master.ConnectionString, await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Integration", "seed.sql")));

        // read-only user that export reads switch to (Collector:ExportReadAsUser default)
        await RunScriptAsync(new SqlConnectionStringBuilder(master.ConnectionString) { InitialCatalog = "DWH" }.ConnectionString,
            "CREATE USER sdc_export_reader WITHOUT LOGIN; ALTER ROLE db_datareader ADD MEMBER sdc_export_reader;");

        var meta = new SqlConnectionStringBuilder(master.ConnectionString) { InitialCatalog = "DataCollectorMeta" }.ConnectionString;
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Metadata"] = meta,
            [$"SqlCredentialOverrides:{SourceServerName}"] = $"User ID={master.UserID};Password={master.Password}",
            [$"SqlCredentialOverrides:{TargetServerName}"] = $"User ID={master.UserID};Password={master.Password}",
            ["Collector:WorkDirectory"] = WorkDirectory,
            ["Collector:KeepLocalFiles"] = "true",
            ["Collector:LocalFileRetentionDays"] = "7",
            ["Collector:DeleteBatchSize"] = "1000",
            ["Collector:TargetTableCompression"] = "NONE",
            ["Gcs:Endpoint"] = GcsBaseUrl,
            ["Gcs:Anonymous"] = "true",
            ["Gcs:MaxRetries"] = "2",
        }).Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddLogging(b => b.AddSimpleConsole().SetMinimumLevel(LogLevel.Warning));
        services.AddSqlDataCollectorCore(config);   // same registration as production hosts
        Services = services.BuildServiceProvider();

        var factory = Services.GetRequiredService<SqlConnectionFactory>();
        await SchemaInstaller.InstallAsync(factory, Services.GetRequiredService<ILogger<CollectorFixture>>(), CancellationToken.None);

        var repo = Services.GetRequiredService<MetadataRepository>();
        SourceServerId = await repo.SaveServerAsync(new SqlServerDef
        {
            Name = SourceServerName, ServerAddress = master.DataSource, IsSource = true, IsTarget = false, TrustServerCertificate = true
        }, "tests");
        TargetServerId = await repo.SaveServerAsync(new SqlServerDef
        {
            Name = TargetServerName, ServerAddress = master.DataSource, IsSource = false, IsTarget = true, TrustServerCertificate = true
        }, "tests");
    }

    public async Task DisposeAsync()
    {
        await Services.DisposeAsync();
        await Task.WhenAll(_sql.DisposeAsync().AsTask(), _gcs.DisposeAsync().AsTask());
        try { Directory.Delete(WorkDirectory, recursive: true); } catch { /* best effort */ }
        Http.Dispose();
    }

    // ------------------------------------------------------------------ helpers for tests

    public MetadataRepository Repo => Services.GetRequiredService<MetadataRepository>();
    public T Get<T>() where T : notnull => Services.GetRequiredService<T>();

    public SqlConnection Db(string database) =>
        new(new SqlConnectionStringBuilder(_sql.GetConnectionString()) { InitialCatalog = database, TrustServerCertificate = true }.ConnectionString);

    public async Task<T> ScalarAsync<T>(string database, string sql, object? args = null)
    {
        await using var c = Db(database);
        return await c.ExecuteScalarAsync<T>(sql, args) ?? default!;
    }

    public async Task ExecAsync(string database, string sql, object? args = null)
    {
        await using var c = Db(database);
        await c.ExecuteAsync(sql, args);
    }

    /// <summary>A new extract job with sensible test defaults (source SrcDb, target DWH).</summary>
    public ExtractJobDef NewExtractJob(string targetTable, Action<ExtractJobDef>? configure = null)
    {
        var j = new ExtractJobDef
        {
            Name = $"test_{targetTable}_{Guid.NewGuid().ToString("N")[..6]}",
            SourceServerId = SourceServerId, SourceDatabase = "SrcDb",
            SourceType = SourceType.Table, SourceSchema = "dbo", SourceTable = "Orders",
            LoadMode = LoadMode.Append, SchemaPolicy = SchemaPolicy.Strict,
            TargetServerId = TargetServerId, TargetDatabase = "DWH", TargetSchema = "stage", TargetTable = targetTable,
            CronExpression = "0 2 * * *", TimeZoneId = "UTC", BatchSize = 1000, CommandTimeoutSec = 300, IsActive = true
        };
        configure?.Invoke(j);
        return j;
    }

    public async Task<ExtractJobDef> SaveAsync(ExtractJobDef job)
    {
        await Repo.SaveExtractJobAsync(job, "tests");
        return (await Repo.GetExtractJobAsync(job.ExtractJobId))!;
    }

    public Task RunExtractAsync(ExtractJobDef job) =>
        Get<ExtractRunner>().RunAsync(job.ExtractJobId, job.Name, "tests", CancellationToken.None);

    public Task RunExportAsync(ExportJobDef job) =>
        Get<ExportRunner>().RunAsync(job.ExportJobId, job.Name, "tests", CancellationToken.None);

    public async Task<IReadOnlyList<(string Name, long Size, string? ContentType)>> ListObjectsAsync(string prefix)
    {
        using var doc = JsonDocument.Parse(await Http.GetStringAsync($"{GcsBaseUrl}/storage/v1/b/{Bucket}/o?prefix={Uri.EscapeDataString(prefix)}"));
        if (!doc.RootElement.TryGetProperty("items", out var items)) return [];
        return items.EnumerateArray()
            .Select(i => (i.GetProperty("name").GetString()!, long.Parse(i.GetProperty("size").GetString()!),
                i.TryGetProperty("contentType", out var ct) ? ct.GetString() : null))
            .OrderBy(x => x.Item1).ToList();
    }

    public Task<string> DownloadTextAsync(string objectName) =>
        Http.GetStringAsync($"{GcsBaseUrl}/storage/v1/b/{Bucket}/o/{Uri.EscapeDataString(objectName)}?alt=media");

    private static async Task RunScriptAsync(string connectionString, string script)
    {
        await using var c = new SqlConnection(connectionString);
        await c.OpenAsync();
        foreach (var batch in GoSeparator().Split(script).Where(b => !string.IsNullOrWhiteSpace(b)))
        {
            await using var cmd = c.CreateCommand();
            cmd.CommandText = batch;
            cmd.CommandTimeout = 300;
            await cmd.ExecuteNonQueryAsync();
        }
    }

    private static int FreeTcpPort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    [GeneratedRegex(@"^\s*GO\s*;?\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex GoSeparator();
}

[CollectionDefinition(Name)]
public sealed class IntegrationCollection : ICollectionFixture<CollectorFixture>
{
    public const string Name = "Integration (Docker)";
}
